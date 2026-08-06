using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed record CatalogBackupRunResult(
    CatalogBackupVerification LocalBackup,
    UsersBackupVerification UsersBackup,
    RemoteCatalogBackupVerification? RemoteBackup,
    Exception? RemoteBackupFailure);

internal sealed class CatalogBackupExecutionLock
{
    public SemaphoreSlim Gate { get; } = new(initialCount: 1, maxCount: 1);
}

/// <summary>Creates, locally restore-verifies, remotely uploads, and remotely checksum-verifies one catalog-and-Users backup set.</summary>
internal sealed class CatalogBackupPipeline(
    CatalogBackupService localBackupService,
    UsersBackupService usersBackupService,
    ICatalogBackupRemoteStore remoteBackupStore,
    CatalogBackupCheckpoint backupCheckpoint,
    CatalogBackupExecutionLock executionLock,
    ILogger<CatalogBackupPipeline> logger)
{
    public async Task<CatalogBackupRunResult> RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[media.backup] waiting for backup execution lock");
        await executionLock.Gate.WaitAsync(cancellationToken);
        try
        {
            var backupSetId = BackupSetTimestamp.Create();
            logger.LogInformation("[media.backup] local catalog and Users snapshot and restore verification started backupSetId={BackupSetId}", backupSetId);
            var localBackup = await localBackupService.CreateAndVerifyAsync(backupSetId, cancellationToken);
            var usersBackup = await usersBackupService.CreateAndVerifyAsync(backupSetId, cancellationToken);
            await VerifyCatalogUsersCoverageAsync(localBackup.SnapshotPath, usersBackup.SnapshotPath, cancellationToken);
            logger.LogInformation(
                "[media.backup] local snapshots verified files={FileCount} folders={FolderCount} blobs={BlobCount} users={UserCount}",
                localBackup.FileCount,
                localBackup.FolderCount,
                localBackup.BlobCount,
                usersBackup.UserCount);
            try
            {
                logger.LogInformation("[media.backup] remote S3 upload and verification started");
                var remoteBackup = await remoteBackupStore.UploadAndVerifyAsync(localBackup, usersBackup, cancellationToken);
                logger.LogInformation(
                    "[media.backup] remote S3 backup verified backupSetId={BackupSetId} artifacts={ArtifactCount} sizeBytes={SizeBytes}",
                    remoteBackup.BackupSetId,
                    remoteBackup.ArtifactCount,
                    remoteBackup.UploadedBytes);
                await backupCheckpoint.MarkVerifiedAsync(cancellationToken);
                return new CatalogBackupRunResult(localBackup, usersBackup, remoteBackup, RemoteBackupFailure: null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "[media.backup] remote S3 backup failed after local verification");
                await backupCheckpoint.MarkFailedAsync("remote_verification_failed", CancellationToken.None);
                return new CatalogBackupRunResult(localBackup, usersBackup, RemoteBackup: null, RemoteBackupFailure: exception);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "[media.backup] backup pipeline failed before remote verification completed");
            await backupCheckpoint.MarkFailedAsync("backup_pipeline_failed", CancellationToken.None);
            throw;
        }
        finally
        {
            executionLock.Gate.Release();
            logger.LogInformation("[media.backup] backup execution lock released");
        }
    }

    internal static async Task VerifyCatalogUsersCoverageAsync(string catalogSnapshotPath, string usersSnapshotPath, CancellationToken cancellationToken)
    {
        await using var catalog = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={catalogSnapshotPath};Mode=ReadOnly");
        await using var users = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={usersSnapshotPath};Mode=ReadOnly");
        await catalog.OpenAsync(cancellationToken);
        await users.OpenAsync(cancellationToken);

        var referencedUserIds = await ReadIdsAsync(catalog, """
            SELECT OwnerUserId FROM libraries
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
            UNION SELECT GrantedByUserId FROM catalog_shares;
            """, cancellationToken);
        var knownUserIds = await ReadIdsAsync(users, "SELECT Id FROM users;", cancellationToken);
        referencedUserIds.ExceptWith(knownUserIds);
        if (referencedUserIds.Count != 0)
        {
            throw new InvalidOperationException($"Catalog backup user-reference check failed for {referencedUserIds.Count} user IDs.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review if the query string passed to 'string SqliteCommand.CommandText' accepts any user input", Justification = "All callers use compile-time coverage queries.")]
    private static async Task<HashSet<string>> ReadIdsAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken)) ids.Add(NormalizeId(reader.GetValue(0)));
        }
        return ids;
    }

    private static string NormalizeId(object value) => value switch
    {
        Guid guid => guid.ToString("D"),
        byte[] bytes when bytes.Length == 16 => new Guid(bytes).ToString("D"),
        string text when Guid.TryParse(text, out var guid) => guid.ToString("D"),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}
