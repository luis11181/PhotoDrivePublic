using System.Collections.Concurrent;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class FileMetadataProcessor(
    MediaCatalogDbContext dbContext,
    IMediaMetadataExtractor extractor,
    IObjectStoreFactory objectStoreFactory,
    IOriginalCache originalCache,
    MediaThumbnailCache thumbnailCache,
    IUploaderIdentityDirectory uploaderIdentityDirectory,
    IConfiguration configuration,
    ILogger<FileMetadataProcessor> logger)
{
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> VersionLocks = new();
    private readonly string _stagingDirectory = Path.GetFullPath(configuration["MediaStorage:LocalDisk:StagingDirectory"] ?? "media-explorer.staging");
    private readonly bool _recoveryManifestsEnabled = BackupStrategyOptions.FromConfiguration(configuration).RecoveryManifestsEnabled;

    public async Task<bool> ProcessVersionAsync(Guid fileVersionId, CancellationToken cancellationToken)
    {
        var gate = VersionLocks.GetOrAdd(fileVersionId, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken)) return false;
        try
        {
            return await ProcessVersionCoreAsync(fileVersionId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ProcessVersionCoreAsync(Guid fileVersionId, CancellationToken cancellationToken)
    {
        var metadata = await dbContext.FileVersionMetadata.SingleOrDefaultAsync(candidate => candidate.FileVersionId == fileVersionId, cancellationToken);
        if (metadata is null) return false;
        if (metadata.State == "processing" && metadata.ProcessingStartedAt > DateTime.UtcNow - ProcessingLease) return false;

        var version = await dbContext.FileVersions.SingleAsync(candidate => candidate.Id == fileVersionId, cancellationToken);
        var file = await dbContext.Files.SingleAsync(candidate => candidate.Id == version.FileId, cancellationToken);
        if (metadata.State == "succeeded" && metadata.ExtractorVersion == ExternalMediaMetadataExtractor.ExtractorVersion)
        {
            await EnsureRecoveryManifestAsync(version, metadata, cancellationToken);
            await CleanupCompletedStagingAsync(file.Id);
            return false;
        }

        metadata.State = "processing";
        metadata.AttemptCount++;
        metadata.ProcessingStartedAt = DateTime.UtcNow;
        metadata.ErrorCode = null;
        metadata.ErrorMessage = null;
        file.MetadataState = "processing";
        file.MetadataAttemptCount = metadata.AttemptCount;
        file.MetadataErrorCode = null;
        file.MetadataErrorMessage = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.metadata] extraction started itemId={ItemId} attempt={Attempt}", file.Id, metadata.AttemptCount);

        var sourcePrepared = false;
        try
        {
            var sourcePath = await EnsureOriginalCachedAsync(file.Id, version, cancellationToken);
            sourcePrepared = true;
            var extracted = await extractor.ExtractAsync(sourcePath, cancellationToken);
            var filenameCapturedAt = ExternalMediaMetadataExtractor.ParseFilenameDate(version.OriginalName);
            var embeddedCapturedAt = extracted.ExifDateTimeOriginal ?? extracted.ExifDateTimeDigitized ?? extracted.EmbeddedCreatedAt;
            var (effectiveCapturedAt, captureDateSource) = ResolveCaptureDate(
                file.UserCapturedAt,
                extracted,
                filenameCapturedAt,
                version.CreatedAt,
                metadata.ImportedCapturedAt);
            var processedAt = DateTime.UtcNow;

            if (IsMedia(extracted.DetectedMimeType))
            {
                try
                {
                    await thumbnailCache.EnsureAsync(
                        version.BlobSha256,
                        extracted.DetectedMimeType,
                        sourcePath,
                        MediaThumbnailCache.GridPixels,
                        cancellationToken);
                    logger.LogInformation("[media.thumbnail] background prewarm completed itemId={ItemId} pixels={Pixels}", file.Id, MediaThumbnailCache.GridPixels);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "[media.thumbnail] background prewarm deferred itemId={ItemId} pixels={Pixels} exceptionType={ExceptionType}",
                        file.Id,
                        MediaThumbnailCache.GridPixels,
                        exception.GetType().Name);
                }
            }

            metadata.State = "succeeded";
            metadata.ProcessingStartedAt = null;
            metadata.ProcessedAt = processedAt;
            metadata.ExtractorVersion = ExternalMediaMetadataExtractor.ExtractorVersion;
            metadata.DetectedMimeType = extracted.DetectedMimeType;
            metadata.MediaFormat = extracted.MediaFormat;
            metadata.Width = extracted.Width;
            metadata.Height = extracted.Height;
            metadata.Orientation = extracted.Orientation;
            metadata.ExifDateTimeOriginal = extracted.ExifDateTimeOriginal;
            metadata.ExifDateTimeDigitized = extracted.ExifDateTimeDigitized;
            metadata.EmbeddedCreatedAt = extracted.EmbeddedCreatedAt;
            metadata.FilenameCapturedAt = filenameCapturedAt;
            metadata.EffectiveCapturedAt = effectiveCapturedAt;
            metadata.CaptureDateSource = captureDateSource;
            metadata.RawMetadataJson = extracted.RawMetadataJson;

            version.MimeType = extracted.DetectedMimeType;
            if (file.CurrentVersionId == version.Id)
            {
                file.MimeType = extracted.DetectedMimeType;
                file.CapturedAt = effectiveCapturedAt;
                file.CaptureDateSource = captureDateSource;
                file.ImportedCapturedAt = metadata.ImportedCapturedAt;
                file.EmbeddedCapturedAt = embeddedCapturedAt;
                file.FilenameCapturedAt = filenameCapturedAt;
                file.Description = metadata.ImportedDescription;
                file.Latitude = metadata.ImportedLatitude;
                file.Longitude = metadata.ImportedLongitude;
                file.AltitudeMeters = metadata.ImportedAltitudeMeters;
                file.Width = extracted.Width;
                file.Height = extracted.Height;
                file.Orientation = extracted.Orientation;
                file.MediaFormat = extracted.MediaFormat;
                file.MetadataState = "succeeded";
                file.MetadataAttemptCount = metadata.AttemptCount;
                file.MetadataProcessedAt = processedAt;
                file.MetadataErrorCode = null;
                file.MetadataErrorMessage = null;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "[media.metadata] extraction completed itemId={ItemId} mimeType={MimeType} captureSource={CaptureSource} width={Width} height={Height} orientation={Orientation}",
                file.Id,
                extracted.DetectedMimeType,
                captureDateSource,
                extracted.Width,
                extracted.Height,
                extracted.Orientation);

            await EnsureRecoveryManifestAsync(version, metadata, cancellationToken);
            await CleanupCompletedStagingAsync(file.Id);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            metadata.State = "pending";
            metadata.ProcessingStartedAt = null;
            file.MetadataState = "pending";
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var filenameCapturedAt = ExternalMediaMetadataExtractor.ParseFilenameDate(version.OriginalName);
            var fallbackCapturedAt = file.UserCapturedAt ?? metadata.ImportedCapturedAt ?? filenameCapturedAt ?? version.CreatedAt;
            var fallbackSource = file.UserCapturedAt.HasValue
                ? "user"
                : metadata.ImportedCapturedAt.HasValue
                    ? "imported_metadata"
                    : filenameCapturedAt.HasValue
                        ? "filename"
                        : "uploaded_at";
            var processedAt = DateTime.UtcNow;
            var (errorCode, publicMessage) = ClassifyFailure(exception);

            metadata.State = "failed";
            metadata.ProcessingStartedAt = null;
            metadata.ProcessedAt = processedAt;
            metadata.ErrorCode = errorCode;
            metadata.ErrorMessage = publicMessage;
            metadata.FilenameCapturedAt = filenameCapturedAt;
            metadata.EffectiveCapturedAt = fallbackCapturedAt;
            metadata.CaptureDateSource = fallbackSource;
            file.CapturedAt = fallbackCapturedAt;
            file.CaptureDateSource = fallbackSource;
            file.ImportedCapturedAt = metadata.ImportedCapturedAt;
            file.FilenameCapturedAt = filenameCapturedAt;
            file.Description = metadata.ImportedDescription;
            file.Latitude = metadata.ImportedLatitude;
            file.Longitude = metadata.ImportedLongitude;
            file.AltitudeMeters = metadata.ImportedAltitudeMeters;
            file.MetadataState = "failed";
            file.MetadataAttemptCount = metadata.AttemptCount;
            file.MetadataProcessedAt = processedAt;
            file.MetadataErrorCode = metadata.ErrorCode;
            file.MetadataErrorMessage = publicMessage;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogError(exception, "[media.metadata] extraction failed itemId={ItemId} attempt={Attempt}; committed original remains available", file.Id, metadata.AttemptCount);
            if (sourcePrepared) await CleanupCompletedStagingAsync(file.Id);
            return false;
        }
    }

    public async Task<int> ProcessPendingBatchAsync(int limit, CancellationToken cancellationToken)
    {
        var staleBefore = DateTime.UtcNow - ProcessingLease;
        var versionIds = new List<Guid>(limit);

        async Task AddCandidatesAsync(IQueryable<Guid> query)
        {
            if (versionIds.Count >= limit) return;
            var candidates = await query.Take(limit - versionIds.Count).ToListAsync(cancellationToken);
            foreach (var candidate in candidates)
            {
                if (!versionIds.Contains(candidate)) versionIds.Add(candidate);
            }
        }

        await AddCandidatesAsync(dbContext.FileVersionMetadata.AsNoTracking()
            .Where(metadata => metadata.State == "pending")
            .OrderBy(metadata => metadata.ProcessedAt)
            .ThenBy(metadata => metadata.FileVersionId)
            .Select(metadata => metadata.FileVersionId));
        await AddCandidatesAsync(dbContext.FileVersionMetadata.AsNoTracking()
            .Where(metadata => metadata.State == "processing" && metadata.ProcessingStartedAt < staleBefore)
            .OrderBy(metadata => metadata.ProcessingStartedAt)
            .ThenBy(metadata => metadata.FileVersionId)
            .Select(metadata => metadata.FileVersionId));
        await AddCandidatesAsync(dbContext.FileVersionMetadata.AsNoTracking()
            .Where(metadata => metadata.State == "succeeded"
                && metadata.ExtractorVersion != ExternalMediaMetadataExtractor.ExtractorVersion)
            .OrderBy(metadata => metadata.ExtractorVersion)
            .ThenBy(metadata => metadata.FileVersionId)
            .Select(metadata => metadata.FileVersionId));
        await AddCandidatesAsync(dbContext.FileVersionMetadata.AsNoTracking()
            .Where(metadata => metadata.State == "succeeded"
                && (metadata.RecoveryManifestState == "pending"
                    || (_recoveryManifestsEnabled && metadata.RecoveryManifestState == "disabled")))
            .OrderBy(metadata => metadata.RecoveryManifestState)
            .ThenBy(metadata => metadata.FileVersionId)
            .Select(metadata => metadata.FileVersionId));

        foreach (var versionId in versionIds) await ProcessVersionAsync(versionId, cancellationToken);
        return versionIds.Count;
    }

    public async Task QueueRetryAsync(Guid fileVersionId, CancellationToken cancellationToken)
    {
        var metadata = await dbContext.FileVersionMetadata.SingleAsync(candidate => candidate.FileVersionId == fileVersionId, cancellationToken);
        var version = await dbContext.FileVersions.SingleAsync(candidate => candidate.Id == fileVersionId, cancellationToken);
        var file = await GetCurrentFileForVersionAsync(version, cancellationToken)
            ?? throw new InvalidOperationException("Only the current file version can be retried.");
        metadata.State = "pending";
        metadata.ProcessingStartedAt = null;
        metadata.ErrorCode = null;
        metadata.ErrorMessage = null;
        file.MetadataState = "pending";
        file.MetadataErrorCode = null;
        file.MetadataErrorMessage = null;
        file.RecoveryManifestState = "pending";
        metadata.RecoveryManifestState = "pending";
        metadata.RecoveryManifestError = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static (DateTime CapturedAt, string Source) ResolveCaptureDate(
        DateTime? userCapturedAt,
        ExtractedMediaMetadata extracted,
        DateTime? filenameCapturedAt,
        DateTime uploadedAt,
        DateTime? importedCapturedAt = null)
    {
        if (userCapturedAt.HasValue) return (userCapturedAt.Value, "user");
        if (importedCapturedAt.HasValue) return (importedCapturedAt.Value, "imported_metadata");
        if (extracted.ExifDateTimeOriginal.HasValue) return (extracted.ExifDateTimeOriginal.Value, "exif_datetime_original");
        if (extracted.ExifDateTimeDigitized.HasValue) return (extracted.ExifDateTimeDigitized.Value, "exif_datetime_digitized");
        if (extracted.EmbeddedCreatedAt.HasValue) return (extracted.EmbeddedCreatedAt.Value, "embedded_creation_time");
        if (filenameCapturedAt.HasValue) return (filenameCapturedAt.Value, "filename");
        return (uploadedAt, "uploaded_at");
    }

    private async Task EnsureRecoveryManifestAsync(FileVersion version, FileVersionMetadata metadata, CancellationToken cancellationToken)
    {
        if (metadata.State != "succeeded" || metadata.RecoveryManifestState == "succeeded" || string.IsNullOrWhiteSpace(metadata.DetectedMimeType)) return;
        if (!_recoveryManifestsEnabled)
        {
            metadata.RecoveryManifestState = "disabled";
            metadata.RecoveryManifestError = null;
            var disabledFile = await GetCurrentFileForVersionAsync(version, cancellationToken);
            if (disabledFile is not null) disabledFile.RecoveryManifestState = "disabled";
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }
        var uploaderEmail = await uploaderIdentityDirectory.GetEmailAsync(version.UploaderUserId, cancellationToken);
        await MediaCatalogService.OriginalMutationGate.WaitAsync(cancellationToken);
        try
        {
            var stillActiveAndReferenced = await dbContext.FileVersions.AsNoTracking()
                .Where(candidate => candidate.Id == version.Id && candidate.BlobSha256 == version.BlobSha256)
                .Join(
                    dbContext.Blobs.AsNoTracking().Where(blob => blob.LifecycleState == Blob.AvailableLifecycleState),
                    candidate => candidate.BlobSha256,
                    blob => blob.Sha256,
                    (_, _) => true)
                .AnyAsync(cancellationToken);
            if (!stillActiveAndReferenced) return;

            try
            {
                metadata.RecoveryManifestState = "processing";
                var file = await GetCurrentFileForVersionAsync(version, cancellationToken);
                if (file is not null) file.RecoveryManifestState = "processing";
                metadata.RecoveryManifestError = null;
                await dbContext.SaveChangesAsync(cancellationToken);
                using var objectStore = objectStoreFactory.Create();
                await objectStore.PutRecoveryManifestIfMissingAsync(new RecoveryManifestData(
                    version.BlobSha256,
                    version.SizeBytes,
                    version.OriginalName,
                    version.DeclaredMimeType,
                    metadata.DetectedMimeType,
                    metadata.MediaFormat,
                    version.UploaderUserId,
                    uploaderEmail,
                    version.CreatedAt,
                    metadata.EffectiveCapturedAt,
                    metadata.CaptureDateSource,
                    metadata.ExifDateTimeOriginal ?? metadata.ExifDateTimeDigitized ?? metadata.EmbeddedCreatedAt,
                    metadata.Width,
                    metadata.Height,
                    metadata.Orientation), cancellationToken);
                metadata.RecoveryManifestState = "succeeded";
                metadata.RecoveryManifestWrittenAt = DateTime.UtcNow;
                if (file is not null) file.RecoveryManifestState = "succeeded";
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogInformation("[media.metadata] recovery manifest verified fileVersionId={FileVersionId}", version.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                metadata.RecoveryManifestState = "pending";
                var file = await GetCurrentFileForVersionAsync(version, CancellationToken.None);
                if (file is not null) file.RecoveryManifestState = "pending";
                await dbContext.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                metadata.RecoveryManifestState = "failed";
                metadata.RecoveryManifestError = "The recovery manifest could not be verified. Retry later.";
                var file = await GetCurrentFileForVersionAsync(version, cancellationToken);
                if (file is not null) file.RecoveryManifestState = "failed";
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogError(exception, "[media.metadata] recovery manifest failed fileVersionId={FileVersionId}; catalog metadata remains committed", version.Id);
            }
        }
        finally
        {
            MediaCatalogService.OriginalMutationGate.Release();
        }
    }

    private async Task<string> EnsureOriginalCachedAsync(Guid itemId, FileVersion version, CancellationToken cancellationToken)
    {
        if (await originalCache.TryOpenReadAsync(version.BlobSha256, version.SizeBytes, cancellationToken) is { } cached)
        {
            await cached.DisposeAsync();
            logger.LogInformation("[media.cache] metadata source hit itemId={ItemId} sizeBytes={SizeBytes}", itemId, version.SizeBytes);
            return originalCache.PathFor(version.BlobSha256);
        }

        var stagedFileName = await dbContext.UploadSessions.AsNoTracking()
            .Where(upload => upload.CatalogFileId == itemId && upload.State == "completed")
            .OrderByDescending(upload => upload.CompletedAt)
            .Select(upload => upload.StagingFileName)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(stagedFileName))
        {
            var stagedPath = Path.Combine(_stagingDirectory, stagedFileName);
            if (File.Exists(stagedPath))
            {
                try
                {
                    await originalCache.EnsureFromFileAsync(version.BlobSha256, stagedPath, version.SizeBytes, cancellationToken);
                    logger.LogInformation("[media.cache] original seeded from completed staging itemId={ItemId} sizeBytes={SizeBytes}", itemId, version.SizeBytes);
                    return originalCache.PathFor(version.BlobSha256);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "[media.cache] staging seed failed itemId={ItemId}; falling back to S3", itemId);
                }
            }
        }

        logger.LogInformation("[media.cache] metadata source miss itemId={ItemId} sizeBytes={SizeBytes}", itemId, version.SizeBytes);
        await originalCache.EnsureAsync(version.BlobSha256, version.SizeBytes, async (destination, token) =>
        {
            using var objectStore = objectStoreFactory.Create();
            using var remote = await objectStore.OpenOriginalAsync(version.BlobSha256, null, null, token);
            await remote.Content.CopyToAsync(destination, token);
        }, cancellationToken);
        return originalCache.PathFor(version.BlobSha256);
    }

    private Task<CatalogFile?> GetCurrentFileForVersionAsync(FileVersion version, CancellationToken cancellationToken) =>
        dbContext.Files.SingleOrDefaultAsync(
            file => file.Id == version.FileId && file.CurrentVersionId == version.Id,
            cancellationToken);

    private async Task CleanupCompletedStagingAsync(Guid itemId)
    {
        var stagedFileNames = await dbContext.UploadSessions.AsNoTracking()
            .Where(upload => upload.CatalogFileId == itemId && upload.State == "completed")
            .Select(upload => upload.StagingFileName)
            .ToListAsync(CancellationToken.None);
        foreach (var stagedFileName in stagedFileNames)
        {
            var path = Path.Combine(_stagingDirectory, stagedFileName);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "[media.upload] completed staging cleanup deferred itemId={ItemId}", itemId);
            }
        }
    }

    private static bool IsMedia(string mimeType) => mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static (string Code, string PublicMessage) ClassifyFailure(Exception exception)
    {
        if (exception is InvalidOperationException && exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ("metadata_timeout", "Metadata extraction timed out. Retry the operation.");
        }
        if (exception is InvalidOperationException)
        {
            return ("metadata_tool_failed", "A metadata tool could not read this file. Verify the configured tools and retry.");
        }
        if (exception is IOException or UnauthorizedAccessException
            || exception.GetType().Namespace?.StartsWith("Amazon", StringComparison.Ordinal) == true)
        {
            return ("original_read_failed", "The stored original could not be read for metadata processing. Retry later.");
        }
        return ("metadata_processing_failed", "Metadata processing failed unexpectedly. Retry later.");
    }
}
