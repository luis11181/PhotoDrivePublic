using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>Runs explicit developer-requested storage diagnostics; it is never scheduled automatically.</summary>
public sealed class MediaMaintenanceService(IServiceProvider serviceProvider, ILogger<MediaMaintenanceService> logger) : IMediaMaintenanceService
{
    public Task<Result<DeveloperBackupStatusRes>> GetBackupStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var freshnessWindowHours = DailyCatalogBackupHostedService.ParseIntervalHours(
            configuration["MediaStorage:Backups:IntervalHours"],
            fallback: 12);
        var status = serviceProvider
            .GetRequiredService<CatalogBackupCheckpoint>()
            .GetStatus(TimeSpan.FromHours(freshnessWindowHours));
        return Task.FromResult<Result<DeveloperBackupStatusRes>>(new DeveloperBackupStatusRes
        {
            LastRemoteVerifiedAt = status.LastRemoteVerifiedAt,
            AgeHours = status.Age?.TotalHours,
            FreshnessWindowHours = freshnessWindowHours,
            State = status.LastRemoteVerifiedAt is null ? "missing" : status.IsFresh ? "current" : "stale"
        });
    }

    public async Task<Result<DeveloperOperationalHealthRes>> GetOperationalHealthAsync(CancellationToken cancellationToken)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var dbContext = serviceProvider.GetRequiredService<MediaCatalogDbContext>();
        var checkpoint = serviceProvider.GetRequiredService<CatalogBackupCheckpoint>();
        var backupWindowHours = DailyCatalogBackupHostedService.ParseIntervalHours(configuration["MediaStorage:Backups:IntervalHours"], 12);
        var backup = checkpoint.GetStatus(TimeSpan.FromHours(backupWindowHours));
        var backupFailure = checkpoint.GetFailureStatus();
        var now = DateTime.UtcNow;
        var staleUploadHours = Math.Clamp(configuration.GetValue("MediaStorage:Health:StaleUploadHours", 24), 1, 168);
        var uploadStaleBefore = now.AddHours(-staleUploadHours);
        var workerStaleBefore = now.AddMinutes(-15);

        var pendingUploads = await dbContext.UploadSessions.CountAsync(session => session.State == "pending", cancellationToken);
        var staleUploads = await dbContext.UploadSessions.CountAsync(session => session.State == "pending" && session.CreatedAt < uploadStaleBefore, cancellationToken);
        var pendingMetadata = await dbContext.FileVersionMetadata.CountAsync(metadata => metadata.State == "pending", cancellationToken);
        var staleMetadata = await dbContext.FileVersionMetadata.CountAsync(metadata => metadata.State == "processing" && metadata.ProcessingStartedAt < workerStaleBefore, cancellationToken);
        var failedMetadata = await dbContext.FileVersionMetadata.CountAsync(metadata => metadata.State == "failed", cancellationToken);
        var repeatedMetadataFailures = await dbContext.FileVersionMetadata.CountAsync(metadata => metadata.State == "failed" && metadata.AttemptCount >= 3, cancellationToken);
        var repeatedImportFailures = await dbContext.ImportItems.CountAsync(item => item.State == "failed" && item.AttemptCount >= 3, cancellationToken);
        var repeatedCacheFailures = await dbContext.CacheBuildItems.CountAsync(item => item.State == "failed" && item.AttemptCount >= 3, cancellationToken);
        var repeatedWorkerFailures = repeatedMetadataFailures + repeatedImportFailures + repeatedCacheFailures;

        var cacheResult = await serviceProvider.GetRequiredService<ICacheBuildService>().GetOptionsAsync(cancellationToken);
        var cache = cacheResult.IsSuccess ? cacheResult.SuccessValue : null;
        var minimumFreeBytes = Math.Max(1, configuration.GetValue("MediaStorage:Health:MinimumFreeDiskBytes", 5L * 1024 * 1024 * 1024));
        var lowDisk = cache is not null && cache.AvailableDiskBytes < minimumFreeBytes;
        var cachePressure = cache is not null && cache.OriginalCacheMaxBytes > 0
            && cache.OriginalCacheBytes >= cache.OriginalCacheMaxBytes * 9 / 10;

        var issues = new List<DeveloperMaintenanceIssueRes>();
        if (backupFailure.ConsecutiveFailureCount > 0) issues.Add(Issue("error", "backup_failed", "The latest verified-backup attempt failed. Review the correlated server logs and run a verified backup."));
        else if (!backup.IsFresh) issues.Add(Issue("error", "backup_stale", "No remotely verified backup is current within the configured backup window."));
        if (staleUploads > 0) issues.Add(Issue("warning", "stale_upload_queue", $"{staleUploads} upload sessions have remained pending for more than {staleUploadHours} hours."));
        if (staleMetadata > 0) issues.Add(Issue("warning", "stale_metadata_queue", $"{staleMetadata} metadata items have exceeded the worker lease."));
        if (failedMetadata > 0) issues.Add(Issue("warning", "metadata_failed", $"{failedMetadata} metadata items are failed and can be retried."));
        if (repeatedWorkerFailures > 0) issues.Add(Issue("error", "repeated_worker_failures", $"{repeatedWorkerFailures} background items have failed at least three times."));
        if (lowDisk) issues.Add(Issue("error", "low_disk_space", "Available local disk space is below the configured safety threshold."));
        if (cachePressure) issues.Add(Issue("warning", "cache_pressure", "The original cache is at least 90% full; eviction pressure is active."));
        if (cache is null) issues.Add(Issue("warning", "capacity_status_unavailable", "Local disk and cache capacity could not be inspected."));

        return new DeveloperOperationalHealthRes
        {
            CheckedAt = now,
            State = issues.Count == 0 ? "healthy" : "attention",
            LastRemoteVerifiedBackupAt = backup.LastRemoteVerifiedAt,
            LastBackupFailureAt = backupFailure.LastFailureAt,
            ConsecutiveBackupFailureCount = backupFailure.ConsecutiveFailureCount,
            PendingUploadCount = pendingUploads,
            StaleUploadCount = staleUploads,
            PendingMetadataCount = pendingMetadata,
            StaleMetadataCount = staleMetadata,
            FailedMetadataCount = failedMetadata,
            RepeatedWorkerFailureCount = repeatedWorkerFailures,
            AvailableDiskBytes = cache?.AvailableDiskBytes ?? 0,
            OriginalCacheBytes = cache?.OriginalCacheBytes ?? 0,
            OriginalCacheMaxBytes = cache?.OriginalCacheMaxBytes ?? 0,
            LowDiskSpace = lowDisk,
            CachePressure = cachePressure,
            Issues = issues
        };
    }

    private static DeveloperMaintenanceIssueRes Issue(string severity, string code, string message) => new()
    {
        Severity = severity,
        Code = code,
        Message = message
    };

    public async Task<Result<DeveloperMaintenanceReportRes>> AuditAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[media.maintenance] developer storage audit started");
        var reconciliation = serviceProvider.GetRequiredService<StorageReconciliationService>();
        var result = await reconciliation.ReconcileAsync(cancellationToken);
        var response = new DeveloperMaintenanceReportRes
        {
            RanAt = DateTime.UtcNow,
            CatalogFileCount = result.CatalogFileCount,
            CatalogFolderCount = result.CatalogFolderCount,
            ReferencedBlobCount = result.ReferencedBlobCount,
            ReferencedBlobBytes = result.ReferencedBlobBytes,
            MissingOriginalCount = result.MissingOriginalCount,
            OrphanedCatalogBlobCount = result.OrphanedCatalogBlobCount,
            OrphanedCatalogBlobBytes = result.OrphanedCatalogBlobBytes,
            PendingPermanentDeletionBlobCount = result.PendingPermanentDeletionBlobCount,
            PendingPermanentDeletionBytes = result.PendingPermanentDeletionBytes,
            UntrackedS3OriginalCount = result.UntrackedS3OriginalCount,
            PendingCleanupCandidateCount = result.CleanupCandidates.Count,
            RemovedCompletedStagingFiles = result.StagingCleanup.RemovedFiles,
            RemovedCompletedStagingBytes = result.StagingCleanup.RemovedBytes,
            OriginalCacheEntryCount = result.CacheEviction.EntryCount,
            OriginalCacheBytes = result.CacheEviction.TotalBytes,
            OriginalCacheLimitBytes = result.CacheEviction.CapacityBytes,
            ThumbnailCacheEntryCount = result.ThumbnailCache.FileCount,
            ThumbnailCacheBytes = result.ThumbnailCache.TotalBytes,
            EvictedCacheEntries = result.CacheEviction.RemovedEntries,
            EvictedCacheBytes = result.CacheEviction.RemovedBytes,
            S3OriginalKeyCount = result.StorageInventory.OriginalKeyCount,
            S3OriginalVersionCount = result.StorageInventory.OriginalVersionCount,
            S3OriginalVersionBytes = result.StorageInventory.OriginalVersionBytes,
            S3RecoveryManifestKeyCount = result.StorageInventory.RecoveryManifestKeyCount,
            S3RecoveryManifestVersionCount = result.StorageInventory.RecoveryManifestVersionCount,
            S3RecoveryManifestVersionBytes = result.StorageInventory.RecoveryManifestVersionBytes,
            S3DeleteMarkerCount = result.StorageInventory.DeleteMarkerCount,
            CleanupCandidates = result.CleanupCandidates,
            Issues = MapIssues(result.Issues)
        };
        logger.LogInformation(
            "[media.maintenance] developer storage audit completed issues={IssueCount}",
            response.Issues.Count);
        return response;
    }

    public async Task<Result<DeveloperBackupReportRes>> BackupAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[media.maintenance] developer verified backup started");
        var backupPipeline = serviceProvider.GetRequiredService<CatalogBackupPipeline>();
        var backup = await backupPipeline.RunAsync(cancellationToken);
        var snapshot = backup.LocalBackup;
        var usersSnapshot = backup.UsersBackup;
        var remoteBackup = backup.RemoteBackup;
        var issues = new List<DeveloperMaintenanceIssueRes>();
        if (backup.RemoteBackupFailure is not null)
        {
            issues.Add(new DeveloperMaintenanceIssueRes
            {
                Severity = "error",
                Code = "remote_catalog_backup_failed",
                Message = "The local catalog backup was verified, but remote backup upload or verification failed. Check the correlated server logs."
            });
        }
        var response = new DeveloperBackupReportRes
        {
            RanAt = DateTime.UtcNow,
            LibraryCount = snapshot.LibraryCount,
            FolderCount = snapshot.FolderCount,
            FileCount = snapshot.FileCount,
            BlobCount = snapshot.BlobCount,
            FileVersionCount = snapshot.FileVersionCount,
            FileVersionMetadataCount = snapshot.FileVersionMetadataCount,
            UserCount = usersSnapshot.UserCount,
            RoleCount = usersSnapshot.RoleCount,
            PermissionCount = usersSnapshot.PermissionCount,
            UserSessionCount = usersSnapshot.UserSessionCount,
            UserTokenCount = usersSnapshot.UserTokenCount,
            RemoteBackupSetId = remoteBackup?.BackupSetId,
            RemoteBackupArtifactCount = remoteBackup?.ArtifactCount ?? 0,
            RemoteBackupBytes = remoteBackup?.UploadedBytes ?? 0,
            RemoteBackupVerified = remoteBackup?.Verified ?? false,
            Issues = issues
        };
        logger.LogInformation("[media.maintenance] developer verified backup completed remoteVerified={RemoteVerified}", response.RemoteBackupVerified);
        return response;
    }

    public async Task<Result<CleanupStorageInconsistenciesRes>> CleanupAsync(CleanupStorageInconsistenciesReq request, CancellationToken cancellationToken)
    {
        var reconciliation = serviceProvider.GetRequiredService<StorageReconciliationService>();
        var validation = await reconciliation.ValidateCleanupSelectionAsync(request, cancellationToken);
        if (validation.IsFailure) return validation.ErrorValue;
        logger.LogInformation("[media.maintenance] production-safe cleanup backup started selectedCount={SelectedCount}", request.CandidateIds?.Count ?? 0);
        var backupPipeline = serviceProvider.GetRequiredService<CatalogBackupPipeline>();
        var backup = await backupPipeline.RunAsync(cancellationToken);
        if (backup.RemoteBackupFailure is not null || backup.RemoteBackup?.Verified != true)
        {
            logger.LogWarning("[media.maintenance] selected cleanup blocked because safety backup was not remotely verified");
            return MediaErrors.Conflict("Selected cleanup was blocked because a new verified remote backup could not be completed.");
        }
        return await reconciliation.CleanupAsync(request, backup.RemoteBackup.BackupSetId, cancellationToken);
    }

    private static List<DeveloperMaintenanceIssueRes> MapIssues(IEnumerable<StorageReconciliationIssue> issues) =>
        issues.Select(issue => new DeveloperMaintenanceIssueRes
        {
            Severity = issue.Severity,
            Code = issue.Code,
            Message = issue.Message
        }).ToList();

}
