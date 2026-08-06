using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

internal sealed record UsersBackupVerification(
    string SnapshotPath,
    string? UsersExportPath,
    long UserCount,
    long RoleCount,
    long PermissionCount,
    long UserSessionCount,
    long UserTokenCount);

/// <summary>Creates a SQLite-consistent snapshot of the separate Users database and proves it can be restored.</summary>
internal sealed class UsersBackupService(IConfiguration configuration)
{
    private static readonly string[] ExportRecordOrder =
    [
        "user", "permission", "role", "role_permission", "user_role", "user_session", "user_token",
        "security_token", "security_audit_event"
    ];
    private readonly string _sourceConnectionString = configuration.GetConnectionString("UsersModule")
        ?? throw new InvalidOperationException("Connection string 'UsersModule' was not configured.");
    private readonly string _backupDirectory = Path.Combine(
        Path.GetFullPath(configuration["MediaStorage:LocalDisk:BackupDirectory"] ?? "media-explorer.backups"),
        "users");
    private readonly int _retainedSnapshotCount = ParsePositiveInt(configuration["MediaStorage:LocalDisk:BackupRetainedSnapshotCount"], 20);
    private readonly bool _portableJsonExportsEnabled = BackupStrategyOptions.FromConfiguration(configuration).PortableJsonExportsEnabled;

    public Task<UsersBackupVerification> CreateAndVerifyAsync(CancellationToken cancellationToken) =>
        CreateAndVerifyAsync(BackupSetTimestamp.Create(), cancellationToken);

    public async Task<UsersBackupVerification> CreateAndVerifyAsync(string backupSetId, CancellationToken cancellationToken)
    {
        BackupSetTimestamp.Validate(backupSetId);
        Directory.CreateDirectory(_backupDirectory);
        var temporarySnapshotPath = Path.Combine(_backupDirectory, $".{Guid.CreateVersion7():N}.db.tmp");
        var snapshotPath = Path.Combine(_backupDirectory, $"users-{backupSetId}.db");
        try
        {
            await CreateConsistentSnapshotAsync(temporarySnapshotPath, cancellationToken);
            File.Move(temporarySnapshotPath, snapshotPath, overwrite: false);
            var verification = await VerifyRestoreAsync(snapshotPath, cancellationToken);
            var usersExportPath = _portableJsonExportsEnabled
                ? await WritePortableUsersExportAsync(snapshotPath, cancellationToken)
                : null;
            RemoveExpiredSnapshots();
            return verification with { UsersExportPath = usersExportPath };
        }
        finally
        {
            if (File.Exists(temporarySnapshotPath)) File.Delete(temporarySnapshotPath);
        }
    }

    public static async Task<UsersBackupVerification> VerifyRestoreAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(snapshotPath)) throw new FileNotFoundException("The Users backup snapshot was not found.", snapshotPath);
        var restorePath = Path.Combine(Path.GetTempPath(), $"media-explorer-users-restore-{Guid.CreateVersion7():N}.db");
        try
        {
            await using (var source = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly;Pooling=False"))
            await using (var restore = new SqliteConnection($"Data Source={restorePath};Pooling=False"))
            {
                await source.OpenAsync(cancellationToken);
                await restore.OpenAsync(cancellationToken);
                source.BackupDatabase(restore);
            }

            await using var restored = new SqliteConnection($"Data Source={restorePath};Mode=ReadOnly;Pooling=False");
            await restored.OpenAsync(cancellationToken);
            await VerifyPragmaAsync(restored, "PRAGMA integrity_check;", "Users backup integrity check", cancellationToken);
            await VerifyPragmaAsync(restored, "PRAGMA foreign_key_check;", "Users backup foreign-key check", cancellationToken);

            return new UsersBackupVerification(
                snapshotPath,
                string.Empty,
                await CountAsync(restored, "users", cancellationToken),
                await CountAsync(restored, "roles", cancellationToken),
                await CountAsync(restored, "permissions", cancellationToken),
                await CountAsync(restored, "user_sessions", cancellationToken),
                await CountAsync(restored, "user_tokens", cancellationToken));
        }
        finally
        {
            DeleteTemporaryDatabase(restorePath);
        }
    }

    private static void DeleteTemporaryDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task CreateConsistentSnapshotAsync(string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new SqliteConnection(_sourceConnectionString);
        await using var destination = new SqliteConnection($"Data Source={destinationPath}");
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private void RemoveExpiredSnapshots()
    {
        foreach (var snapshot in new DirectoryInfo(_backupDirectory).EnumerateFiles("users-*.db")
                     .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                     .Skip(_retainedSnapshotCount))
        {
            var timestamp = Path.GetFileNameWithoutExtension(snapshot.Name).Replace("users-", string.Empty, StringComparison.Ordinal);
            snapshot.Delete();
            var exportDirectory = Path.Combine(_backupDirectory, $"users-export-{timestamp}");
            if (Directory.Exists(exportDirectory)) Directory.Delete(exportDirectory, recursive: true);
        }
    }

    private static async Task<string> WritePortableUsersExportAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        var timestamp = Path.GetFileNameWithoutExtension(snapshotPath).Replace("users-", string.Empty, StringComparison.Ordinal);
        var exportDirectory = Path.Combine(Path.GetDirectoryName(snapshotPath)!, $"users-export-{timestamp}");
        Directory.CreateDirectory(exportDirectory);
        var exportPath = Path.Combine(exportDirectory, "users-v1.jsonl");
        var temporaryExportPath = $"{exportPath}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            await using var connection = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken);
            await using (var stream = new FileStream(temporaryExportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await WriteRecordAsync(writer, new { recordType = "export", formatVersion = 1, createdAt = DateTime.UtcNow, recordOrder = ExportRecordOrder });
                await WriteQueryAsync(connection, writer, "SELECT * FROM users ORDER BY Id;", "user", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT * FROM permissions ORDER BY Id;", "permission", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT * FROM roles ORDER BY Id;", "role", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT * FROM role_permissions ORDER BY permission_id, role_id;", "role_permission", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT * FROM user_roles ORDER BY role_id, user_id;", "user_role", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT * FROM user_sessions ORDER BY Id;", "user_session", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT * FROM user_tokens ORDER BY Id;", "user_token", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT * FROM security_tokens ORDER BY Id;", "security_token", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT * FROM security_audit_events ORDER BY OccurredAt, Id;", "security_audit_event", cancellationToken);
            }
            File.Move(temporaryExportPath, exportPath, overwrite: true);
            await WriteChecksumAsync(exportPath, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(exportDirectory, "RECOVERY-README.md"), RecoveryReadme(timestamp), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            return exportPath;
        }
        finally
        {
            if (File.Exists(temporaryExportPath)) File.Delete(temporaryExportPath);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review if the query string passed to 'string SqliteCommand.CommandText' accepts any user input", Justification = "All callers provide compile-time export queries.")]
    private static async Task WriteQueryAsync(SqliteConnection connection, StreamWriter writer, string sql, string recordType, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                fields[ToCamelCase(reader.GetName(ordinal))] = await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetValue(ordinal);
            }
            await WriteRecordAsync(writer, new { recordType, fields });
        }
    }

    private static Task WriteRecordAsync(StreamWriter writer, object record) => writer.WriteLineAsync(JsonSerializer.Serialize(record));

    private static async Task WriteChecksumAsync(string exportPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(exportPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(exportPath)!, "users-v1.sha256"),
            $"{checksum}  users-v1.jsonl{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static string RecoveryReadme(string timestamp) => $"""
        # Media Explorer portable Users export

        Format version: 1
        Created from SQLite snapshot: {timestamp}
        Data file: `users-v1.jsonl` (UTF-8, uncompressed JSON Lines)
        Checksum: `users-v1.sha256` (SHA-256 of the data file)

        Validate the checksum before recovery. Read records in the order declared by the `export` header. Preserve user UUIDs exactly before restoring the catalog so library ownership, upload, and favorite-state references remain valid.

        This private recovery export contains user profile fields, OAuth-provider links, password hashes, sessions, reset/recovery token hashes, security audit events, and authorization assignments. It must never be exposed through browser APIs or logs.
        """;

    private static string ToCamelCase(string value) => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review if the query string passed to 'string SqliteCommand.CommandText' accepts any user input", Justification = "All callers use compile-time SQLite PRAGMA statements.")]
    private static async Task VerifyPragmaAsync(SqliteConnection connection, string statement, string checkName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var result = reader.GetString(0);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{checkName} failed: {result}.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review if the query string passed to 'string SqliteCommand.CommandText' accepts any user input", Justification = "The switch permits only compile-time Users table names.")]
    private static async Task<long> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = table switch
        {
            "users" => "SELECT COUNT(*) FROM users;",
            "roles" => "SELECT COUNT(*) FROM roles;",
            "permissions" => "SELECT COUNT(*) FROM permissions;",
            "user_sessions" => "SELECT COUNT(*) FROM user_sessions;",
            "user_tokens" => "SELECT COUNT(*) FROM user_tokens;",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}

internal static class BackupSetTimestamp
{
    public static string Create() => DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);

    public static void Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9]{8}T[0-9]{9}Z$"))
        {
            throw new ArgumentException("The backup set identifier must use the UTC timestamp convention yyyyMMddTHHmmssfffZ.", nameof(value));
        }
    }
}
