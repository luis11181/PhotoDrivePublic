using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed record StorageReconciliationIssue(string Severity, string Code, string Message);
internal sealed record StorageReconciliationReport(
    long CatalogFileCount,
    long CatalogFolderCount,
    int ReferencedBlobCount,
    long ReferencedBlobBytes,
    int MissingOriginalCount,
    int OrphanedCatalogBlobCount,
    long OrphanedCatalogBlobBytes,
    int PendingPermanentDeletionBlobCount,
    long PendingPermanentDeletionBytes,
    int UntrackedS3OriginalCount,
    StorageInventorySummary StorageInventory,
    StagingCleanupResult StagingCleanup,
    OriginalCacheEvictionResult CacheEviction,
    DirectoryStorageSummary ThumbnailCache,
    IReadOnlyList<StorageCleanupCandidateRes> CleanupCandidates,
    IReadOnlyList<StorageReconciliationIssue> Issues);
internal sealed record StagingCleanupResult(int RemovedFiles, long RemovedBytes);
internal sealed record DirectoryStorageSummary(int FileCount, long TotalBytes);

/// <summary>Explicit developer inventory and reviewed cleanup for catalog/S3 inconsistencies; never scheduled.</summary>
internal sealed class StorageReconciliationService(
    MediaCatalogDbContext dbContext,
    IObjectStoreFactory objectStoreFactory,
    IOriginalCache originalCache,
    IConfiguration configuration,
    ILogger<StorageReconciliationService> logger)
{
    private readonly string _stagingDirectory = Path.GetFullPath(configuration["MediaStorage:LocalDisk:StagingDirectory"] ?? "media-explorer.staging");
    private readonly string _thumbnailDirectory = Path.GetFullPath(configuration["MediaStorage:LocalDisk:ThumbnailDirectory"] ?? "media-explorer.thumbnails");

    public async Task<StorageReconciliationReport> ReconcileAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[media.maintenance] reconciliation started");
        var issues = new List<StorageReconciliationIssue>();
        var catalogFileCount = await dbContext.Files.LongCountAsync(cancellationToken);
        var catalogFolderCount = await dbContext.Folders.LongCountAsync(cancellationToken);
        var referencedBlobHashes = await dbContext.FileVersions.AsNoTracking()
            .Select(version => version.BlobSha256)
            .Distinct()
            .ToListAsync(cancellationToken);
        var referencedBlobHashSet = new HashSet<string>(referencedBlobHashes, StringComparer.Ordinal);
        var catalogBlobs = await dbContext.Blobs.AsNoTracking()
            .Select(blob => new CatalogBlob(blob.Sha256, blob.SizeBytes, blob.LifecycleState))
            .ToListAsync(cancellationToken);
        var catalogBlobHashSet = new HashSet<string>(catalogBlobs.Select(blob => blob.Sha256), StringComparer.Ordinal);
        var referencedCatalogBlobs = catalogBlobs
            .Where(blob => referencedBlobHashSet.Contains(blob.Sha256))
            .ToArray();
        var referencedBlobBytes = referencedCatalogBlobs.Sum(blob => blob.SizeBytes);
        var orphanedCatalogBlobs = catalogBlobs
            .Where(blob => !referencedBlobHashSet.Contains(blob.Sha256))
            .ToArray();
        var orphanedCatalogBlobBytes = orphanedCatalogBlobs.Sum(blob => blob.SizeBytes);
        var pendingPermanentDeletionBlobs = catalogBlobs
            .Where(blob => blob.LifecycleState == Blob.PendingPermanentDeletionLifecycleState)
            .ToArray();
        var pendingPermanentDeletionBytes = pendingPermanentDeletionBlobs.Sum(blob => blob.SizeBytes);
        if (orphanedCatalogBlobs.Length > 0)
        {
            issues.Add(new StorageReconciliationIssue("warning", "orphaned_catalog_blob", $"{orphanedCatalogBlobs.Length} catalog blob rows have no file-version reference. Select them below to review permanent cleanup."));
            logger.LogWarning("[media.maintenance] orphaned catalog blobs found count={OrphanedCatalogBlobCount}", orphanedCatalogBlobs.Length);
        }

        var missingOriginalCount = 0;
        StorageInventory inventory;
        using (var objectStore = objectStoreFactory.Create())
        {
            foreach (var hash in referencedBlobHashes)
            {
                if (await objectStore.OriginalExistsAsync(hash, cancellationToken)) continue;
                missingOriginalCount++;
                issues.Add(new StorageReconciliationIssue("error", "missing_s3_original", "The catalog references an original that was not found in S3."));
                logger.LogError("[media.maintenance] missing S3 original found; hash omitted from normal logs");
            }
            inventory = await objectStore.ListOriginalsForReconciliationAsync(cancellationToken);
        }
        var referencedStorageWithoutBlobRow = inventory.Originals
            .Where(original => !catalogBlobHashSet.Contains(original.Sha256) && referencedBlobHashSet.Contains(original.Sha256))
            .ToArray();
        foreach (var _ in referencedStorageWithoutBlobRow)
            issues.Add(new StorageReconciliationIssue("error", "referenced_original_missing_blob_row", "A file version references stored original bytes whose catalog blob row is missing. Permanent cleanup is blocked."));
        var untrackedS3Originals = inventory.Originals
            .Where(original => !catalogBlobHashSet.Contains(original.Sha256) && !referencedBlobHashSet.Contains(original.Sha256))
            .ToArray();
        if (untrackedS3Originals.Length > 0)
        {
            issues.Add(new StorageReconciliationIssue("warning", "untracked_s3_original", $"{untrackedS3Originals.Length} S3 originals or recovery manifests have no catalog blob row. Select them below to review permanent cleanup."));
            logger.LogWarning("[media.maintenance] untracked S3 originals found count={UntrackedS3OriginalCount}", untrackedS3Originals.Length);
        }

        var cleanupCandidates = await SynchronizeCleanupCandidatesAsync(orphanedCatalogBlobs, untrackedS3Originals, cancellationToken);
        var stagingCleanup = await RemoveCompletedStagingFilesAsync(cancellationToken);
        var eviction = await originalCache.EvictAsync(cancellationToken);
        var thumbnailCache = InspectThumbnailCache(issues, cancellationToken);
        logger.LogInformation(
            "[media.maintenance] reconciliation completed referencedOriginals={ReferencedCount} missingOriginals={MissingCount} orphanedBlobs={OrphanedCount} untrackedS3Originals={UntrackedCount} cleanupCandidates={CleanupCandidateCount} stagingFilesRemoved={StagingRemoved} cacheEntriesEvicted={CacheEntriesEvicted} cacheBytesEvicted={CacheBytesEvicted}",
            referencedBlobHashes.Count,
            missingOriginalCount,
            orphanedCatalogBlobs.Length,
            untrackedS3Originals.Length,
            cleanupCandidates.Count,
            stagingCleanup.RemovedFiles,
            eviction.RemovedEntries,
            eviction.RemovedBytes);
        return new StorageReconciliationReport(
            catalogFileCount,
            catalogFolderCount,
            referencedBlobHashes.Count,
            referencedBlobBytes,
            missingOriginalCount,
            orphanedCatalogBlobs.Length,
            orphanedCatalogBlobBytes,
            pendingPermanentDeletionBlobs.Length,
            pendingPermanentDeletionBytes,
            untrackedS3Originals.Length,
            inventory.Summary,
            stagingCleanup,
            eviction,
            thumbnailCache,
            cleanupCandidates,
            issues);
    }

    public async Task<Result<CleanupStorageInconsistenciesRes>> CleanupAsync(CleanupStorageInconsistenciesReq request, string safetyBackupSetId, CancellationToken cancellationToken)
    {
        var selectedIds = (request.CandidateIds ?? []).Distinct().ToArray();
        var deletedCandidateCount = 0;
        var resolvedCandidateCount = 0;
        var failedCandidateCount = 0;
        await MediaCatalogService.OriginalMutationGate.WaitAsync(cancellationToken);
        try
        {
            var validation = await ValidateCleanupSelectionAsync(request, cancellationToken);
            if (validation.IsFailure) return validation.ErrorValue;
            var candidates = await dbContext.StorageCleanupCandidates
                .Where(candidate => selectedIds.Contains(candidate.Id) && candidate.State == StorageCleanupCandidate.PendingState)
                .ToListAsync(cancellationToken);
            using var objectStore = objectStoreFactory.Create();
            foreach (var candidate in candidates)
            {
                if (candidate.Kind is not (StorageCleanupCandidate.OrphanedCatalogBlobKind or StorageCleanupCandidate.UntrackedS3OriginalKind))
                {
                    candidate.Error = "Unexpected cleanup-candidate type. It was not deleted.";
                    await dbContext.SaveChangesAsync(cancellationToken);
                    logger.LogCritical("[media.maintenance] unexpected cleanup-candidate type blocked");
                    failedCandidateCount++;
                    continue;
                }
                var blob = await dbContext.Blobs.SingleOrDefaultAsync(item => item.Sha256 == candidate.BlobSha256, cancellationToken);
                var hasReference = await dbContext.FileVersions.AsNoTracking()
                    .AnyAsync(version => version.BlobSha256 == candidate.BlobSha256, cancellationToken);
                if (hasReference)
                {
                    candidate.Error = "Permanent cleanup is blocked because a catalog file-version reference exists.";
                    if (blob is not null) blob.DeletionError = candidate.Error;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    logger.LogCritical("[media.maintenance] selected permanent cleanup blocked by file-version reference");
                    failedCandidateCount++;
                    continue;
                }
                if (candidate.Kind == StorageCleanupCandidate.OrphanedCatalogBlobKind && (blob is null || hasReference))
                {
                    dbContext.StorageCleanupCandidates.Remove(candidate);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    resolvedCandidateCount++;
                    continue;
                }
                if (candidate.Kind == StorageCleanupCandidate.UntrackedS3OriginalKind && blob is not null)
                {
                    dbContext.StorageCleanupCandidates.Remove(candidate);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    resolvedCandidateCount++;
                    continue;
                }

                candidate.AttemptCount++;
                candidate.LastAttemptAt = DateTime.UtcNow;
                if (blob is not null)
                {
                    blob.DeletionAttemptCount++;
                    blob.DeletionLastAttemptAt = candidate.LastAttemptAt;
                }
                try
                {
                    await objectStore.DeleteOriginalPermanentlyAsync(candidate.BlobSha256, cancellationToken);
                    await RemoveCachedDerivativesAsync(candidate.BlobSha256, cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    candidate.Error = "Permanent storage deletion failed. Select this candidate again to retry.";
                    if (blob is not null) blob.DeletionError = candidate.Error;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    logger.LogError("[media.maintenance] selected permanent cleanup failed; candidate remains pending");
                    failedCandidateCount++;
                    continue;
                }

                if (blob is not null)
                {
                    var reappearedReference = await dbContext.FileVersions.AsNoTracking()
                        .AnyAsync(version => version.BlobSha256 == candidate.BlobSha256, cancellationToken);
                    if (reappearedReference)
                    {
                        candidate.Error = "A catalog reference appeared during storage cleanup. The candidate remains for investigation.";
                        blob.DeletionError = candidate.Error;
                        await dbContext.SaveChangesAsync(cancellationToken);
                        logger.LogCritical("[media.maintenance] catalog reference appeared during selected permanent cleanup");
                        failedCandidateCount++;
                        continue;
                    }
                    dbContext.Blobs.Remove(blob);
                }
                dbContext.StorageCleanupCandidates.Remove(candidate);
                await dbContext.SaveChangesAsync(cancellationToken);
                deletedCandidateCount++;
            }
        }
        finally
        {
            MediaCatalogService.OriginalMutationGate.Release();
        }

        var remainingCandidates = await PendingCandidatesAsync(cancellationToken);
        logger.LogInformation(
            "[media.maintenance] selected storage cleanup completed deleted={DeletedCandidateCount} resolved={ResolvedCandidateCount} failed={FailedCandidateCount} remaining={RemainingCandidateCount}",
            deletedCandidateCount,
            resolvedCandidateCount,
            failedCandidateCount,
            remainingCandidates.Count);
        return new CleanupStorageInconsistenciesRes
        {
            SafetyBackupSetId = safetyBackupSetId,
            DeletedCandidateCount = deletedCandidateCount,
            ResolvedCandidateCount = resolvedCandidateCount,
            FailedCandidateCount = failedCandidateCount,
            RemainingCandidates = remainingCandidates
        };
    }

    public async Task<Result> ValidateCleanupSelectionAsync(CleanupStorageInconsistenciesReq request, CancellationToken cancellationToken)
    {
        if (!request.Confirmed) return MediaErrors.Validation("Confirm the selected storage inconsistency cleanup.");
        if (!string.Equals(request.ConfirmationPhrase, "DELETE INCONSISTENCIES", StringComparison.Ordinal))
            return MediaErrors.Validation("Type DELETE INCONSISTENCIES to confirm permanent cleanup.");
        var selectedIds = (request.CandidateIds ?? []).Distinct().ToArray();
        if (selectedIds.Length == 0) return MediaErrors.Validation("Select at least one storage inconsistency to clean.");
        if (request.ExpectedCandidateCount != selectedIds.Length || request.ExpectedCandidateCount < 0)
            return MediaErrors.Validation("The reviewed cleanup-candidate count must match the selected candidates.");

        var selected = await dbContext.StorageCleanupCandidates.AsNoTracking()
            .Where(candidate => selectedIds.Contains(candidate.Id) && candidate.State == StorageCleanupCandidate.PendingState)
            .Select(candidate => candidate.SizeBytes)
            .ToListAsync(cancellationToken);
        if (selected.Count != selectedIds.Length)
            return MediaErrors.Conflict("The selected storage inconsistencies changed. Run developer maintenance again and review the current list.");
        if (request.ExpectedCandidateBytes.HasValue
            && request.ExpectedCandidateBytes.Value != selected.Sum())
            return MediaErrors.Conflict("The selected storage inconsistency sizes changed. Run developer maintenance again and review the current list.");
        return Result.Success();
    }

    private async Task<IReadOnlyList<StorageCleanupCandidateRes>> SynchronizeCleanupCandidatesAsync(
        IEnumerable<CatalogBlob> orphanedCatalogBlobs,
        IEnumerable<StorageOriginalDescriptor> untrackedS3Originals,
        CancellationToken cancellationToken)
    {
        var discovered = orphanedCatalogBlobs
            .Select(blob => new DiscoveredCandidate(blob.Sha256, StorageCleanupCandidate.OrphanedCatalogBlobKind, blob.SizeBytes))
            .Concat(untrackedS3Originals.Select(original => new DiscoveredCandidate(original.Sha256, StorageCleanupCandidate.UntrackedS3OriginalKind, original.VersionBytes)))
            .ToArray();
        var existing = await dbContext.StorageCleanupCandidates.ToListAsync(cancellationToken);
        var existingByKey = existing.ToDictionary(candidate => (candidate.BlobSha256, candidate.Kind));
        var discoveredKeys = new HashSet<(string BlobSha256, string Kind)>();
        var now = DateTime.UtcNow;
        foreach (var candidate in discovered)
        {
            var key = (candidate.BlobSha256, candidate.Kind);
            discoveredKeys.Add(key);
            if (existingByKey.TryGetValue(key, out var storedCandidate))
            {
                storedCandidate.SizeBytes = candidate.SizeBytes;
                storedCandidate.DetectedAt = now;
                continue;
            }
            dbContext.StorageCleanupCandidates.Add(new StorageCleanupCandidate
            {
                BlobSha256 = candidate.BlobSha256,
                Kind = candidate.Kind,
                SizeBytes = candidate.SizeBytes,
                DetectedAt = now
            });
        }
        // A successful object deletion can be followed by a local derivative-cleanup failure. Keep that
        // durable, failed candidate even after the next inventory no longer finds the S3 object so an
        // explicit retry can finish the cache/thumbnail cleanup.
        dbContext.StorageCleanupCandidates.RemoveRange(existing.Where(candidate =>
            !discoveredKeys.Contains((candidate.BlobSha256, candidate.Kind))
            && candidate.AttemptCount == 0
            && string.IsNullOrWhiteSpace(candidate.Error)));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await PendingCandidatesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<StorageCleanupCandidateRes>> PendingCandidatesAsync(CancellationToken cancellationToken) =>
        await dbContext.StorageCleanupCandidates.AsNoTracking()
            .Where(candidate => candidate.State == StorageCleanupCandidate.PendingState)
            .OrderBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.DetectedAt)
            .Select(candidate => new StorageCleanupCandidateRes
            {
                Id = candidate.Id,
                Kind = candidate.Kind,
                SizeBytes = candidate.SizeBytes,
                DetectedAt = candidate.DetectedAt,
                AttemptCount = candidate.AttemptCount,
                Error = candidate.Error
            })
            .ToListAsync(cancellationToken);

    private async Task RemoveCachedDerivativesAsync(string sha256, CancellationToken cancellationToken)
    {
        await originalCache.RemoveAsync(sha256, cancellationToken);
        var thumbnailDirectory = Path.Combine(_thumbnailDirectory, "sha256", sha256);
        if (Directory.Exists(thumbnailDirectory)) Directory.Delete(thumbnailDirectory, recursive: true);
    }

    private async Task<StagingCleanupResult> RemoveCompletedStagingFilesAsync(CancellationToken cancellationToken)
    {
        var completedUploads = await dbContext.UploadSessions.AsNoTracking()
            .Where(upload => upload.State == "completed")
            .Select(upload => upload.StagingFileName)
            .ToListAsync(cancellationToken);
        var removed = 0;
        long removedBytes = 0;
        foreach (var stagingFileName in completedUploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(_stagingDirectory, stagingFileName);
            if (!File.Exists(path)) continue;
            removedBytes += new FileInfo(path).Length;
            File.Delete(path);
            removed++;
        }
        return new StagingCleanupResult(removed, removedBytes);
    }

    private DirectoryStorageSummary InspectThumbnailCache(List<StorageReconciliationIssue> issues, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_thumbnailDirectory)) return new DirectoryStorageSummary(0, 0);
        try
        {
            var files = Directory.EnumerateFiles(_thumbnailDirectory, "*", SearchOption.AllDirectories);
            var count = 0;
            long totalBytes = 0;
            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalBytes += new FileInfo(path).Length;
                count++;
            }
            return new DirectoryStorageSummary(count, totalBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(new StorageReconciliationIssue("warning", "thumbnail_cache_inspection_failed", "Thumbnail-cache usage could not be inspected. Check the correlated server logs."));
            logger.LogWarning(exception, "[media.maintenance] thumbnail cache inspection failed");
            return new DirectoryStorageSummary(0, 0);
        }
    }

    private sealed record DiscoveredCandidate(string BlobSha256, string Kind, long SizeBytes);

    private sealed record CatalogBlob(string Sha256, long SizeBytes, string LifecycleState);
}
