using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CSSApps.Shared.Infrastructure.Data.Turso;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TursoConnection = Turso.Data.Sqlite.SqliteConnection;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>Counts proven by restore verification of one database snapshot.</summary>
internal sealed record DatabaseBackupVerification(
    string BackupSetId,
    string SnapshotPath,
    string ManifestPath,
    long SnapshotBytes,
    string SnapshotSha256,
    long LibraryCount,
    long FolderCount,
    long FileCount,
    long BlobCount,
    long FileVersionCount,
    long FileVersionMetadataCount,
    long UserCount,
    long RoleCount,
    long UserSessionCount,
    long UserTokenCount);

internal sealed record DatabaseBackupRunResult(
    DatabaseBackupVerification Local,
    RemoteDatabaseBackupVerification? Remote,
    Exception? RemoteFailure);

/// <summary>Serializes backup runs inside one process.</summary>
internal sealed class DatabaseBackupExecutionLock
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>
/// Creates one independent, verified copy of the whole PhotoDrive database (catalog and Users) and stores it in the
/// private S3 backup bucket. Turso Cloud is the primary safety net; this weekly copy protects against losing the
/// Turso database itself.
/// <list type="number">
/// <item>Pull this host's replica so it matches the cloud, then <c>VACUUM INTO</c> a consistent snapshot.</item>
/// <item>Convert the snapshot to a plain SQLite file in WAL mode without Turso sync bookkeeping, the format Turso
/// imports and any SQLite tool opens.</item>
/// <item>Verify integrity, foreign keys, file/version/metadata coverage, and that every catalog user exists.</item>
/// <item>Upload the snapshot and a manifest, verify their checksums remotely, and record the success in the cloud.</item>
/// </list>
/// </summary>
internal sealed class DatabaseBackupService(
    TursoDatabase database,
    IDatabaseBackupRemoteStore remoteStore,
    ClusterCoordinator cluster,
    DatabaseBackupExecutionLock executionLock,
    IConfiguration configuration,
    ILogger<DatabaseBackupService> logger)
{
    // Sync bookkeeping copied by VACUUM INTO from a replica; meaningless (and misleading) in a restored database.
    private static readonly string[] SyncBookkeepingTables = ["turso_cdc", "turso_cdc_version", "turso_sync_last_change_id"];

    private readonly string _backupDirectory = Path.Combine(
        Path.GetFullPath(configuration["MediaStorage:LocalDisk:BackupDirectory"] ?? "media-explorer.backups"),
        "database");
    private readonly int _retainedLocalSets = Math.Max(1, configuration.GetValue("MediaStorage:LocalDisk:BackupRetainedSnapshotCount", 2));

    public async Task<DatabaseBackupRunResult> RunAsync(CancellationToken cancellationToken)
    {
        await executionLock.Gate.WaitAsync(cancellationToken);
        try
        {
            var backupSetId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            logger.LogInformation("[media.backup] database snapshot started backupSetId={BackupSetId}", backupSetId);
            DatabaseBackupVerification local;
            try
            {
                local = await CreateAndVerifyAsync(backupSetId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "[media.backup] database snapshot or verification failed");
                await TryRecordFailureAsync("snapshot_verification_failed");
                throw;
            }

            logger.LogInformation(
                "[media.backup] snapshot verified files={FileCount} folders={FolderCount} users={UserCount} bytes={Bytes}",
                local.FileCount, local.FolderCount, local.UserCount, local.SnapshotBytes);
            try
            {
                var remote = await remoteStore.UploadAndVerifyAsync(local, cancellationToken);
                await cluster.RecordBackupSucceededAsync(backupSetId, cancellationToken);
                logger.LogInformation("[media.backup] remote backup verified backupSetId={BackupSetId} bytes={Bytes}", backupSetId, remote.UploadedBytes);
                return new DatabaseBackupRunResult(local, remote, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "[media.backup] remote S3 backup failed after local verification");
                await TryRecordFailureAsync("remote_verification_failed");
                return new DatabaseBackupRunResult(local, null, exception);
            }
        }
        finally
        {
            executionLock.Gate.Release();
        }
    }

    private async Task<DatabaseBackupVerification> CreateAndVerifyAsync(string backupSetId, CancellationToken cancellationToken)
    {
        var setDirectory = Path.Combine(_backupDirectory, backupSetId);
        Directory.CreateDirectory(setDirectory);
        var snapshotPath = Path.Combine(setDirectory, "photodrive.db");

        await database.PullAsync(cancellationToken);
        await database.SnapshotAsync(snapshotPath, cancellationToken);
        await ConvertToPortableSqliteAsync(snapshotPath, cancellationToken);
        var verification = await VerifyAsync(backupSetId, snapshotPath, cancellationToken);
        await File.WriteAllTextAsync(verification.ManifestPath, ManifestJson(verification), cancellationToken);
        RemoveExpiredLocalSets();
        return verification;
    }

    private static async Task ConvertToPortableSqliteAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        // Unpooled: a pooled handle keeps the file locked after dispose on Windows-backed Docker bind mounts.
        await using var connection = new TursoConnection($"Data Source={snapshotPath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        var tables = await ReadStringsAsync(connection, "SELECT name FROM sqlite_schema WHERE type = 'table';", cancellationToken);
        foreach (var table in SyncBookkeepingTables.Where(name => tables.Contains(name, StringComparer.OrdinalIgnoreCase)))
            await ExecuteAsync(connection, $"DROP TABLE \"{table}\";", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode = wal;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
    }

    internal static async Task<DatabaseBackupVerification> VerifyAsync(string backupSetId, string snapshotPath, CancellationToken cancellationToken)
    {
        await using var connection = new TursoConnection($"Data Source={snapshotPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);

        var integrity = await ReadStringsAsync(connection, "PRAGMA integrity_check;", cancellationToken);
        if (integrity.Count != 1 || !string.Equals(integrity[0], "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Database backup integrity check failed.");
        await DatabaseForeignKeyCheck.VerifyAsync(connection, cancellationToken);

        var tables = await ReadStringsAsync(connection, "SELECT name FROM sqlite_schema WHERE type = 'table';", cancellationToken);
        foreach (var required in new[] { TursoDatabase.MediaHistoryTable, TursoDatabase.UsersHistoryTable, "libraries", "files", "users" })
        {
            if (!tables.Contains(required, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"Database backup is missing required table '{required}'.");
        }

        var fileVersions = await CountAsync(connection, "SELECT COUNT(*) FROM file_versions;", cancellationToken);
        var metadata = await CountAsync(connection, "SELECT COUNT(*) FROM file_version_metadata;", cancellationToken);
        if (metadata != fileVersions)
            throw new InvalidOperationException($"Database backup metadata coverage check failed: {metadata} metadata rows for {fileVersions} file versions.");
        var missingCurrentVersions = await CountAsync(
            connection,
            "SELECT COUNT(*) FROM files LEFT JOIN file_versions ON file_versions.Id = files.CurrentVersionId WHERE file_versions.Id IS NULL;",
            cancellationToken);
        if (missingCurrentVersions != 0)
            throw new InvalidOperationException($"Database backup current-version coverage check failed for {missingCurrentVersions} files.");
        var unknownUsers = await CountAsync(
            connection,
            """
            SELECT COUNT(*) FROM (
                SELECT OwnerUserId AS UserId FROM libraries
                UNION SELECT UploaderUserId FROM file_versions
                UNION SELECT AddedByUserId FROM album_items
                UNION SELECT CreatedByUserId FROM timeline_events
                UNION SELECT CreatedByUserId FROM tags
                UNION SELECT AddedByUserId FROM file_tags
                UNION SELECT AddedByUserId FROM folder_tags
                UNION SELECT UserId FROM file_user_states
                UNION SELECT UserId FROM folder_user_states
                UNION SELECT UserId FROM upload_sessions
                UNION SELECT RecipientUserId FROM catalog_shares
                UNION SELECT GrantedByUserId FROM catalog_shares
            ) referenced
            WHERE referenced.UserId IS NOT NULL
              AND upper(referenced.UserId) NOT IN (SELECT upper(Id) FROM users);
            """,
            cancellationToken);
        if (unknownUsers != 0)
            throw new InvalidOperationException($"Database backup user-reference check failed for {unknownUsers} user IDs.");

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in new[] { "libraries", "folders", "files", "blobs", "users", "roles", "user_sessions", "user_tokens" })
            counts[table] = await CountAsync(connection, $"SELECT COUNT(*) FROM {table};", cancellationToken);

        // Hash only after the database file is closed: an open database holds a lock that blocks reading it.
        await connection.DisposeAsync();
        var file = new FileInfo(snapshotPath);
        string sha256;
        await using (var stream = file.OpenRead())
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));

        return new DatabaseBackupVerification(
            backupSetId,
            snapshotPath,
            Path.Combine(file.DirectoryName!, "manifest.json"),
            file.Length,
            sha256,
            counts["libraries"],
            counts["folders"],
            counts["files"],
            counts["blobs"],
            fileVersions,
            metadata,
            counts["users"],
            counts["roles"],
            counts["user_sessions"],
            counts["user_tokens"]);
    }

    private static string ManifestJson(DatabaseBackupVerification verification) => JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        backupSetId = verification.BackupSetId,
        createdAt = DateTime.UtcNow,
        application = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        database = new
        {
            file = "photodrive.db",
            format = "SQLite 3, WAL journal mode (importable with `turso db import`)",
            bytes = verification.SnapshotBytes,
            sha256 = verification.SnapshotSha256
        },
        counts = new
        {
            libraries = verification.LibraryCount,
            folders = verification.FolderCount,
            files = verification.FileCount,
            blobs = verification.BlobCount,
            fileVersions = verification.FileVersionCount,
            users = verification.UserCount
        },
        restore = "See docs/RESTORE_DRILL.md. Search tables are rebuilt automatically on first start."
    }, new JsonSerializerOptions { WriteIndented = true });

    private void RemoveExpiredLocalSets()
    {
        foreach (var expired in new DirectoryInfo(_backupDirectory).EnumerateDirectories()
                     .OrderByDescending(directory => directory.Name, StringComparer.Ordinal)
                     .Skip(_retainedLocalSets))
        {
            expired.Delete(recursive: true);
        }
    }

    private async Task TryRecordFailureAsync(string code)
    {
        try
        {
            await cluster.RecordBackupFailedAsync(code, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[media.backup] could not record the backup failure in the shared database");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only compile-time statements are executed.")]
    private static async Task ExecuteAsync(TursoConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // PRAGMA statements return a status row; drain it.
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only compile-time statements are executed.")]
    private static async Task<long> CountAsync(TursoConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only compile-time statements are executed.")]
    private static async Task<List<string>> ReadStringsAsync(TursoConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            values.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty);
        return values;
    }
}
