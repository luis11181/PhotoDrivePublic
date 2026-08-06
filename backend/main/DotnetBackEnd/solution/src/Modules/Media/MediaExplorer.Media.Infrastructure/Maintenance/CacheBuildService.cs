using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class CacheBuildService(
    MediaCatalogDbContext dbContext,
    IObjectStoreFactory objectStoreFactory,
    IOriginalCache originalCache,
    MediaThumbnailCache thumbnailCache,
    CacheBuildWorkSignal workSignal,
    IConfiguration configuration,
    ILogger<CacheBuildService> logger) : ICacheBuildService
{
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(15);
    private readonly string _workingDirectory = Path.Combine(
        Path.GetFullPath(configuration["MediaStorage:LocalDisk:StagingDirectory"] ?? "media-explorer.staging"),
        "cache-build");

    public async Task<Result<CacheBuildOptionsRes>> GetOptionsAsync(CancellationToken cancellationToken)
    {
        var status = await originalCache.InspectAsync(cancellationToken);
        var catalog = await LoadCatalogAsync(cancellationToken);
        var catalogFileCount = await dbContext.Files.AsNoTracking()
            .LongCountAsync(cancellationToken);
        var referencedBlobHashes = dbContext.FileVersions.AsNoTracking()
            .Select(version => version.BlobSha256)
            .Distinct();
        var storedOriginalBytes = await dbContext.Blobs.AsNoTracking()
            .Where(blob => referencedBlobHashes.Contains(blob.Sha256))
            .Select(blob => (long?)blob.SizeBytes)
            .SumAsync(cancellationToken) ?? 0;
        return new CacheBuildOptionsRes
        {
            OriginalCacheMaxBytes = status.CapacityBytes,
            OriginalCacheBytes = status.TotalBytes,
            OriginalCacheEntryCount = status.EntryCount,
            AvailableDiskBytes = status.AvailableDiskBytes,
            CatalogFileCount = catalogFileCount,
            StoredOriginalBytes = storedOriginalBytes,
            CatalogMediaItemCount = catalog.SelectedItemCount,
            DistinctOriginalCount = catalog.Blobs.Count,
            GridThumbnailCount = catalog.Blobs.Count(blob => thumbnailCache.Exists(blob.Sha256, MediaThumbnailCache.GridPixels)),
            CachedOriginalCount = catalog.Blobs.Count(blob => originalCache.Exists(blob.Sha256, blob.SizeBytes))
        };
    }

    public async Task<Result<CacheBuildPreviewRes>> PreviewAsync(PreviewCacheBuildReq request, CancellationToken cancellationToken)
    {
        var planResult = await BuildPlanAsync(cancellationToken);
        if (planResult.IsFailure) return planResult.ErrorValue;
        return await ToPreviewAsync(planResult.SuccessValue, request.KeepOriginals, cancellationToken);
    }

    public async Task<Result<CacheBuildJobRes>> StartAsync(Guid userId, StartCacheBuildReq request, CancellationToken cancellationToken)
    {
        var operationActive = await dbContext.CacheBuildJobs.AnyAsync(
            job => job.State == CacheBuildJob.PendingState
                || job.State == CacheBuildJob.RunningState
                || job.State == CacheBuildJob.CancellingState,
            cancellationToken);
        if (operationActive) return MediaErrors.Conflict("A catalog cache build is already running.");

        var planResult = await BuildPlanAsync(cancellationToken);
        if (planResult.IsFailure) return planResult.ErrorValue;
        var plan = planResult.SuccessValue;
        var preview = await ToPreviewAsync(plan, request.KeepOriginals, cancellationToken);
        if (preview.SelectedItemCount != request.ExpectedSelectedItemCount
            || preview.QueuedItemCount != request.ExpectedQueuedItemCount
            || preview.DownloadBytes != request.ExpectedDownloadBytes)
        {
            return MediaErrors.Conflict("The cache-build preview changed. Review the current counts before starting.");
        }
        if (preview.QueuedItemCount == 0) return MediaErrors.Conflict("The catalog cache is already current.");

        var previousOperations = await dbContext.CacheBuildJobs
            .Where(job => job.State != CacheBuildJob.PendingState
                && job.State != CacheBuildJob.RunningState
                && job.State != CacheBuildJob.CancellingState)
            .ToListAsync(cancellationToken);
        dbContext.CacheBuildJobs.RemoveRange(previousOperations);

        var now = DateTime.UtcNow;
        var job = new CacheBuildJob
        {
            RequestedByUserId = userId,
            KeepOriginals = request.KeepOriginals,
            SelectedItemCount = preview.SelectedItemCount,
            QueuedItemCount = preview.QueuedItemCount,
            PlannedDownloadBytes = preview.DownloadBytes,
            CreatedAt = now
        };
        dbContext.CacheBuildJobs.Add(job);
        dbContext.CacheBuildItems.AddRange(plan.Blobs
            .Where(blob => NeedsWork(blob, request.KeepOriginals))
            .Select(blob => new CacheBuildItem
            {
                CacheBuildJobId = job.Id,
                CatalogFileId = blob.FileId,
                BlobSha256 = blob.Sha256,
                MimeType = blob.MimeType,
                SizeBytes = blob.SizeBytes
            }));
        await dbContext.SaveChangesAsync(cancellationToken);
        workSignal.Signal();
        logger.LogInformation(
            "[media.cache-build] operation created operationId={OperationId} selectedItems={SelectedItems} queuedOriginals={QueuedOriginals} plannedDownloadBytes={PlannedDownloadBytes} keepOriginals={KeepOriginals}",
            job.Id,
            job.SelectedItemCount,
            job.QueuedItemCount,
            job.PlannedDownloadBytes,
            job.KeepOriginals);
        return await ToResponseAsync(job, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<CacheBuildJobRes>>> ListAsync(CancellationToken cancellationToken)
    {
        var jobs = await dbContext.CacheBuildJobs.AsNoTracking()
            .OrderByDescending(job => job.CreatedAt)
            .Take(1)
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyList<CacheBuildJobRes>>.Success(await ToResponsesAsync(jobs, cancellationToken));
    }

    public async Task<Result<CacheBuildJobRes>> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.CacheBuildJobs.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
        return job is null
            ? MediaErrors.NotFound("The cache-build job was not found.")
            : await ToResponseAsync(job, cancellationToken);
    }

    public async Task<Result<CacheBuildJobRes>> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.CacheBuildJobs.SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
        if (job is null) return MediaErrors.NotFound("The cache-build job was not found.");
        if (job.State is not (CacheBuildJob.PendingState or CacheBuildJob.RunningState))
            return MediaErrors.Conflict("Only a pending or running cache build can be cancelled.");
        job.State = CacheBuildJob.CancellingState;
        await dbContext.SaveChangesAsync(cancellationToken);
        workSignal.Signal();
        return await ToResponseAsync(job, cancellationToken);
    }

    public async Task<Result<CacheBuildJobRes>> RetryFailedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.CacheBuildJobs.SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
        if (job is null) return MediaErrors.NotFound("The cache-build job was not found.");
        var failed = await dbContext.CacheBuildItems
            .Where(item => item.CacheBuildJobId == jobId && item.State == CacheBuildItem.FailedState)
            .ToListAsync(cancellationToken);
        if (failed.Count == 0) return MediaErrors.Conflict("This cache build has no failed items to retry.");
        foreach (var item in failed)
        {
            item.State = CacheBuildItem.PendingState;
            item.ProcessingStartedAt = null;
            item.CompletedAt = null;
            item.Error = null;
        }
        job.State = CacheBuildJob.RunningState;
        job.CompletedAt = null;
        job.Error = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        workSignal.Signal();
        return await ToResponseAsync(job, cancellationToken);
    }

    internal async Task<int> ProcessPendingBatchAsync(int limit, CancellationToken cancellationToken)
    {
        await FinalizeCancellingJobsAsync(cancellationToken);
        var processed = 0;
        for (var index = 0; index < limit; index++)
        {
            var staleBefore = DateTime.UtcNow - ProcessingLease;
            var item = await dbContext.CacheBuildItems
                .Where(candidate => candidate.State == CacheBuildItem.PendingState
                    || (candidate.State == CacheBuildItem.ProcessingState && candidate.ProcessingStartedAt < staleBefore))
                .Join(
                    dbContext.CacheBuildJobs.Where(job => job.State == CacheBuildJob.PendingState || job.State == CacheBuildJob.RunningState),
                    candidate => candidate.CacheBuildJobId,
                    job => job.Id,
                    (candidate, _) => candidate)
                .OrderBy(candidate => candidate.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (item is null) break;

            var job = await dbContext.CacheBuildJobs.SingleAsync(candidate => candidate.Id == item.CacheBuildJobId, cancellationToken);
            job.State = CacheBuildJob.RunningState;
            job.StartedAt ??= DateTime.UtcNow;
            item.State = CacheBuildItem.ProcessingState;
            item.ProcessingStartedAt = DateTime.UtcNow;
            item.AttemptCount++;
            item.Error = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            try
            {
                var worked = await ProcessItemAsync(job, item, cancellationToken);
                item.State = worked ? CacheBuildItem.CompletedState : CacheBuildItem.SkippedState;
                item.CompletedAt = DateTime.UtcNow;
                item.ProcessingStartedAt = null;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                item.State = CacheBuildItem.PendingState;
                item.ProcessingStartedAt = null;
                await dbContext.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                item.State = CacheBuildItem.FailedState;
                item.ProcessingStartedAt = null;
                item.CompletedAt = DateTime.UtcNow;
                item.Error = "The cache item could not be built. Retry it after checking the correlated server logs.";
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogError(
                    "[media.cache-build] item failed jobId={JobId} cacheBuildItemId={CacheBuildItemId} exceptionType={ExceptionType}",
                    job.Id,
                    item.Id,
                    exception.GetType().Name);
            }
            processed++;
        }
        await FinalizeCompletedJobsAsync(cancellationToken);
        return processed;
    }

    private async Task<bool> ProcessItemAsync(CacheBuildJob job, CacheBuildItem item, CancellationToken cancellationToken)
    {
        var originalWasCached = originalCache.Exists(item.BlobSha256, item.SizeBytes);
        var thumbnailWasCached = thumbnailCache.Exists(item.BlobSha256, MediaThumbnailCache.GridPixels);
        if (originalWasCached && thumbnailWasCached) return false;
        if (!job.KeepOriginals && thumbnailWasCached) return false;

        string? temporaryPath = null;
        try
        {
            string sourcePath;
            if (job.KeepOriginals)
            {
                await EnsureOriginalAsync(item, cancellationToken);
                sourcePath = originalCache.PathFor(item.BlobSha256);
            }
            else if (originalWasCached)
            {
                sourcePath = originalCache.PathFor(item.BlobSha256);
            }
            else
            {
                Directory.CreateDirectory(_workingDirectory);
                temporaryPath = Path.Combine(_workingDirectory, $"{item.Id:N}.source");
                await DownloadTemporaryOriginalAsync(item, temporaryPath, cancellationToken);
                sourcePath = temporaryPath;
            }

            if (!thumbnailWasCached)
            {
                await thumbnailCache.EnsureAsync(
                    item.BlobSha256,
                    item.MimeType,
                    sourcePath,
                    MediaThumbnailCache.GridPixels,
                    cancellationToken);
            }
            return true;
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private Task EnsureOriginalAsync(CacheBuildItem item, CancellationToken cancellationToken) =>
        originalCache.EnsureAsync(item.BlobSha256, item.SizeBytes, async (destination, token) =>
        {
            using var objectStore = objectStoreFactory.Create();
            using var remote = await objectStore.OpenOriginalAsync(item.BlobSha256, null, null, token);
            await remote.Content.CopyToAsync(destination, token);
        }, cancellationToken);

    private async Task DownloadTemporaryOriginalAsync(CacheBuildItem item, string path, CancellationToken cancellationToken)
    {
        using var objectStore = objectStoreFactory.Create();
        using var remote = await objectStore.OpenOriginalAsync(item.BlobSha256, null, null, cancellationToken);
        await using (var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await remote.Content.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            if (destination.Length != item.SizeBytes) throw new IOException("The temporary original length did not match the catalog.");
        }
    }

    private async Task<Result<CachePlan>> BuildPlanAsync(CancellationToken cancellationToken)
    {
        return await LoadCatalogAsync(cancellationToken);
    }

    private async Task<CachePlan> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        IQueryable<CatalogFile> files = dbContext.Files.AsNoTracking()
            .Where(file => file.TrashedAt == null
                && (file.MimeType.StartsWith("image/") || file.MimeType.StartsWith("video/")));

        var candidates = await (
            from file in files
            join version in dbContext.FileVersions.AsNoTracking() on file.CurrentVersionId equals version.Id
            select new CacheBlob(file.Id, version.BlobSha256, file.MimeType, file.SizeBytes))
            .ToListAsync(cancellationToken);
        var blobs = candidates.GroupBy(candidate => candidate.Sha256, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        return new CachePlan(candidates.Count, blobs);
    }

    private async Task<CacheBuildPreviewRes> ToPreviewAsync(CachePlan plan, bool keepOriginals, CancellationToken cancellationToken)
    {
        var status = await originalCache.InspectAsync(cancellationToken);
        var missingThumbnailCount = plan.Blobs.Count(blob => !thumbnailCache.Exists(blob.Sha256, MediaThumbnailCache.GridPixels));
        var missingOriginals = plan.Blobs.Where(blob => !originalCache.Exists(blob.Sha256, blob.SizeBytes)).ToList();
        var queued = plan.Blobs.Where(blob => NeedsWork(blob, keepOriginals)).ToList();
        var downloadBytes = queued.Where(blob => !originalCache.Exists(blob.Sha256, blob.SizeBytes)).Sum(blob => blob.SizeBytes);
        var retainedBytes = keepOriginals ? missingOriginals.Sum(blob => blob.SizeBytes) : 0;
        return new CacheBuildPreviewRes
        {
            SelectedItemCount = plan.SelectedItemCount,
            QueuedItemCount = queued.Count,
            MissingThumbnailCount = missingThumbnailCount,
            MissingOriginalCount = missingOriginals.Count,
            DownloadBytes = downloadBytes,
            KeepOriginals = keepOriginals,
            OriginalCacheMaxBytes = status.CapacityBytes,
            OriginalCacheBytes = status.TotalBytes,
            AvailableDiskBytes = status.AvailableDiskBytes,
            FitsConfiguredCache = retainedBytes <= Math.Max(0, status.CapacityBytes - status.TotalBytes)
        };
    }

    private bool NeedsWork(CacheBlob blob, bool keepOriginals) =>
        !thumbnailCache.Exists(blob.Sha256, MediaThumbnailCache.GridPixels)
        || (keepOriginals && !originalCache.Exists(blob.Sha256, blob.SizeBytes));

    private async Task FinalizeCancellingJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = await dbContext.CacheBuildJobs.Where(job => job.State == CacheBuildJob.CancellingState).ToListAsync(cancellationToken);
        var staleBefore = DateTime.UtcNow - ProcessingLease;
        foreach (var job in jobs)
        {
            var cancellable = await dbContext.CacheBuildItems
                .Where(item => item.CacheBuildJobId == job.Id
                    && (item.State == CacheBuildItem.PendingState
                        || (item.State == CacheBuildItem.ProcessingState && item.ProcessingStartedAt < staleBefore)))
                .ToListAsync(cancellationToken);
            foreach (var item in cancellable)
            {
                item.State = CacheBuildItem.CancelledState;
                item.ProcessingStartedAt = null;
                item.CompletedAt = DateTime.UtcNow;
            }
            var processing = await dbContext.CacheBuildItems.AnyAsync(
                item => item.CacheBuildJobId == job.Id && item.State == CacheBuildItem.ProcessingState,
                cancellationToken);
            if (!processing)
            {
                job.State = CacheBuildJob.CancelledState;
                job.CompletedAt = DateTime.UtcNow;
            }
        }
        if (jobs.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task FinalizeCompletedJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = await dbContext.CacheBuildJobs
            .Where(job => job.State == CacheBuildJob.RunningState || job.State == CacheBuildJob.PendingState)
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            var unfinished = await dbContext.CacheBuildItems.AnyAsync(
                item => item.CacheBuildJobId == job.Id
                    && (item.State == CacheBuildItem.PendingState || item.State == CacheBuildItem.ProcessingState),
                cancellationToken);
            if (unfinished) continue;
            var failed = await dbContext.CacheBuildItems.AnyAsync(
                item => item.CacheBuildJobId == job.Id && item.State == CacheBuildItem.FailedState,
                cancellationToken);
            job.State = failed ? CacheBuildJob.CompletedWithErrorsState : CacheBuildJob.CompletedState;
            job.CompletedAt = DateTime.UtcNow;
        }
        if (jobs.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<CacheBuildJobRes> ToResponseAsync(CacheBuildJob job, CancellationToken cancellationToken) =>
        (await ToResponsesAsync([job], cancellationToken))[0];

    private async Task<List<CacheBuildJobRes>> ToResponsesAsync(IReadOnlyList<CacheBuildJob> jobs, CancellationToken cancellationToken)
    {
        var jobIds = jobs.Select(job => job.Id).ToArray();
        var states = await dbContext.CacheBuildItems.AsNoTracking()
            .Where(item => jobIds.Contains(item.CacheBuildJobId))
            .GroupBy(item => new { item.CacheBuildJobId, item.State })
            .Select(group => new { group.Key.CacheBuildJobId, group.Key.State, Count = group.Count() })
            .ToListAsync(cancellationToken);
        int Count(Guid id, string state) => states.FirstOrDefault(item => item.CacheBuildJobId == id && item.State == state)?.Count ?? 0;
        return jobs.Select(job => new CacheBuildJobRes
        {
            Id = job.Id,
            KeepOriginals = job.KeepOriginals,
            State = job.State,
            SelectedItemCount = job.SelectedItemCount,
            QueuedItemCount = job.QueuedItemCount,
            PendingCount = Count(job.Id, CacheBuildItem.PendingState),
            ProcessingCount = Count(job.Id, CacheBuildItem.ProcessingState),
            CompletedCount = Count(job.Id, CacheBuildItem.CompletedState),
            SkippedCount = Count(job.Id, CacheBuildItem.SkippedState),
            FailedCount = Count(job.Id, CacheBuildItem.FailedState),
            CancelledCount = Count(job.Id, CacheBuildItem.CancelledState),
            PlannedDownloadBytes = job.PlannedDownloadBytes,
            Error = job.Error,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt
        }).ToList();
    }

    private sealed record CacheBlob(Guid FileId, string Sha256, string MimeType, long SizeBytes);
    private sealed record CachePlan(int SelectedItemCount, IReadOnlyList<CacheBlob> Blobs);
}
