using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

internal sealed record CatalogBackupVerification(
    string SnapshotPath,
    string? CatalogExportPath,
    long LibraryCount,
    long FolderCount,
    long FileCount,
    long BlobCount,
    long FileVersionCount,
    long FileVersionMetadataCount);

/// <summary>Creates a SQLite-consistent catalog snapshot and proves it can be restored before retaining it.</summary>
internal sealed class CatalogBackupService(MediaCatalogDbContext dbContext, IConfiguration configuration)
{
    private static readonly string[] ExportRecordOrder = ["library", "folder", "file", "file_version", "file_version_metadata", "album", "album_item", "timeline_event", "tag", "file_tag", "folder_tag", "catalog_share", "folder_user_state", "file_user_state"];
    private readonly string _backupDirectory = Path.Combine(
        Path.GetFullPath(configuration["MediaStorage:LocalDisk:BackupDirectory"] ?? "media-explorer.backups"),
        "catalog");
    private readonly int _retainedSnapshotCount = ParsePositiveInt(configuration["MediaStorage:LocalDisk:BackupRetainedSnapshotCount"], 20);
    private readonly bool _portableJsonExportsEnabled = BackupStrategyOptions.FromConfiguration(configuration).PortableJsonExportsEnabled;

    public Task<CatalogBackupVerification> CreateAndVerifyAsync(CancellationToken cancellationToken) =>
        CreateAndVerifyAsync(BackupSetTimestamp.Create(), cancellationToken);

    public async Task<CatalogBackupVerification> CreateAndVerifyAsync(string backupSetId, CancellationToken cancellationToken)
    {
        BackupSetTimestamp.Validate(backupSetId);
        Directory.CreateDirectory(_backupDirectory);
        var temporarySnapshotPath = Path.Combine(_backupDirectory, $".{Guid.CreateVersion7():N}.db.tmp");
        var snapshotPath = Path.Combine(_backupDirectory, $"catalog-{backupSetId}.db");
        try
        {
            await CreateConsistentSnapshotAsync(temporarySnapshotPath, cancellationToken);
            File.Move(temporarySnapshotPath, snapshotPath, overwrite: false);
            var verification = await VerifyRestoreAsync(snapshotPath, cancellationToken);
            var catalogExportPath = _portableJsonExportsEnabled
                ? await WritePortableCatalogExportAsync(snapshotPath, cancellationToken)
                : null;
            RemoveExpiredSnapshots();
            return verification with { CatalogExportPath = catalogExportPath };
        }
        finally
        {
            if (File.Exists(temporarySnapshotPath)) File.Delete(temporarySnapshotPath);
        }
    }

    public static async Task<CatalogBackupVerification> VerifyRestoreAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(snapshotPath)) throw new FileNotFoundException("The catalog backup snapshot was not found.", snapshotPath);
        var restorePath = Path.Combine(Path.GetTempPath(), $"media-explorer-catalog-restore-{Guid.CreateVersion7():N}.db");
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
            await VerifyPragmaAsync(restored, "PRAGMA integrity_check;", "Catalog backup integrity check", cancellationToken);
            await VerifyPragmaAsync(restored, "PRAGMA foreign_key_check;", "Catalog backup foreign-key check", cancellationToken);

            var fileVersionCount = await CountAsync(restored, "file_versions", cancellationToken);
            var metadataCount = await CountAsync(restored, "file_version_metadata", cancellationToken);
            if (metadataCount != fileVersionCount)
            {
                throw new InvalidOperationException($"Catalog backup metadata coverage check failed: {metadataCount} metadata rows for {fileVersionCount} file versions.");
            }
            await using var currentVersionCommand = restored.CreateCommand();
            currentVersionCommand.CommandText = "SELECT COUNT(*) FROM files LEFT JOIN file_versions ON file_versions.Id = files.CurrentVersionId WHERE file_versions.Id IS NULL;";
            var missingCurrentVersions = (long)(await currentVersionCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (missingCurrentVersions != 0)
            {
                throw new InvalidOperationException($"Catalog backup current-version coverage check failed for {missingCurrentVersions} files.");
            }

            return new CatalogBackupVerification(
                snapshotPath,
                string.Empty,
                await CountAsync(restored, "libraries", cancellationToken),
                await CountAsync(restored, "folders", cancellationToken),
                await CountAsync(restored, "files", cancellationToken),
                await CountAsync(restored, "blobs", cancellationToken),
                fileVersionCount,
                metadataCount);
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
        var sourceConnectionString = dbContext.Database.GetDbConnection().ConnectionString;
        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection($"Data Source={destinationPath}");
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private void RemoveExpiredSnapshots()
    {
        var snapshots = new DirectoryInfo(_backupDirectory).EnumerateFiles("catalog-*.db")
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(_retainedSnapshotCount);
        foreach (var snapshot in snapshots)
        {
            var timestamp = Path.GetFileNameWithoutExtension(snapshot.Name).Replace("catalog-", string.Empty, StringComparison.Ordinal);
            snapshot.Delete();
            var exportDirectory = Path.Combine(_backupDirectory, $"catalog-export-{timestamp}");
            if (Directory.Exists(exportDirectory)) Directory.Delete(exportDirectory, recursive: true);
        }
    }

    private static async Task<string> WritePortableCatalogExportAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        var timestamp = Path.GetFileNameWithoutExtension(snapshotPath).Replace("catalog-", string.Empty, StringComparison.Ordinal);
        var exportDirectory = Path.Combine(Path.GetDirectoryName(snapshotPath)!, $"catalog-export-{timestamp}");
        Directory.CreateDirectory(exportDirectory);
        var exportPath = Path.Combine(exportDirectory, "catalog-v1.jsonl");
        var temporaryExportPath = $"{exportPath}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            await using var connection = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken);
            await using (var stream = new FileStream(temporaryExportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await WriteRecordAsync(writer, new
                {
                    recordType = "export",
                    formatVersion = 1,
                    createdAt = DateTime.UtcNow,
                    recordOrder = ExportRecordOrder
                });
                await WriteQueryAsync(connection, writer, "SELECT Id, OwnerUserId, Name, CreatedAt FROM libraries ORDER BY Id;", "library", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT Id, LibraryId, ParentFolderId, Name, SystemRole, CreatedAt, ModifiedAt, TrashedAt FROM folders ORDER BY Sequence;", "folder", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT Id, LibraryId, ParentFolderId, Name, MimeType, SizeBytes, CreatedAt, ModifiedAt, CapturedAt, CaptureDateSource, UserCapturedAt, ImportedCapturedAt, EmbeddedCapturedAt, FilenameCapturedAt, Description, Latitude, Longitude, AltitudeMeters, Width, Height, Orientation, MediaFormat, MetadataState, MetadataAttemptCount, MetadataErrorCode, MetadataErrorMessage, MetadataProcessedAt, RecoveryManifestState, CurrentVersionId, TrashedAt FROM files ORDER BY Sequence;", "file", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT Id, FileId, BlobSha256, SizeBytes, MimeType, DeclaredMimeType, OriginalName, UploaderUserId, CreatedAt FROM file_versions ORDER BY CreatedAt, Id;", "file_version", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT FileVersionId, State, AttemptCount, ProcessingStartedAt, ProcessedAt, ExtractorVersion, ErrorCode, ErrorMessage, DetectedMimeType, MediaFormat, Width, Height, Orientation, ExifDateTimeOriginal, ExifDateTimeDigitized, EmbeddedCreatedAt, FilenameCapturedAt, ImportedCapturedAt, ImportedDescription, ImportedLatitude, ImportedLongitude, ImportedAltitudeMeters, EffectiveCapturedAt, CaptureDateSource, RawMetadataJson, RecoveryManifestState, RecoveryManifestError, RecoveryManifestWrittenAt FROM file_version_metadata ORDER BY FileVersionId;", "file_version_metadata", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT Id, LibraryId, Name, Description, CreatedAt, ModifiedAt FROM albums ORDER BY CreatedAt, Id;", "album", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT AlbumId, FileId, AddedByUserId, AddedAt FROM album_items ORDER BY AlbumId, AddedAt, FileId;", "album_item", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT Id, LibraryId, Title, StartDate, EndDate, Description, CreatedByUserId, CreatedAt, ModifiedAt FROM timeline_events ORDER BY LibraryId, StartDate, Id;", "timeline_event", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT Id, LibraryId, Name, NormalizedName, Color, CreatedByUserId, CreatedAt, ModifiedAt FROM tags ORDER BY LibraryId, Name, Id;", "tag", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT FileId, TagId, AddedByUserId, AddedAt FROM file_tags ORDER BY FileId, TagId;", "file_tag", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT FolderId, TagId, AddedByUserId, AddedAt FROM folder_tags ORDER BY FolderId, TagId;", "folder_tag", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT Id, LibraryId, FileId, FolderId, AlbumId, RecipientUserId, GrantedByUserId, Role, CreatedAt, ModifiedAt FROM catalog_shares ORDER BY CreatedAt, Id;", "catalog_share", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT UserId, FolderId, Favorite, ModifiedAt FROM folder_user_states ORDER BY UserId, FolderId;", "folder_user_state", cancellationToken);
                await WriteQueryAsync(connection, writer, "SELECT UserId, FileId, Favorite, ModifiedAt FROM file_user_states ORDER BY UserId, FileId;", "file_user_state", cancellationToken);
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All callers provide compile-time export queries.")]
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
            Path.Combine(Path.GetDirectoryName(exportPath)!, "catalog-v1.sha256"),
            $"{checksum}  catalog-v1.jsonl{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static string RecoveryReadme(string timestamp) => $"""
        # Media Explorer portable catalog export

        Format version: 1
        Created from SQLite snapshot: {timestamp}
        Data file: `catalog-v1.jsonl` (UTF-8, uncompressed JSON Lines)
        Checksum: `catalog-v1.sha256` (SHA-256 of the data file)

        Validate the checksum before recovery. Read records in the order declared by the `export` header, recreate libraries and protected system folders, then files, file versions, and Photos-library membership. Original bytes are addressed by each file-version `blobSha256` and remain in private object storage at `blobs/sha256/<hash>`.

        File-version metadata records preserve detected MIME/format, dimensions, orientation, raw embedded metadata, capture-date candidates and precedence source separately from the file's future user-corrected capture date.

        This export contains catalog metadata, Photos and album relationships, timeline events, private catalog grants, and per-user favorite state, but never credentials, sessions, password hashes, or S3 credentials.
        """;

    private static string ToCamelCase(string value) => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All callers use compile-time SQLite PRAGMA statements.")]
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The switch permits only compile-time table names.")]
    private static async Task<long> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = table switch
        {
            "libraries" => "SELECT COUNT(*) FROM libraries;",
            "folders" => "SELECT COUNT(*) FROM folders;",
            "files" => "SELECT COUNT(*) FROM files;",
            "blobs" => "SELECT COUNT(*) FROM blobs;",
            "file_versions" => "SELECT COUNT(*) FROM file_versions;",
            "file_version_metadata" => "SELECT COUNT(*) FROM file_version_metadata;",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}
