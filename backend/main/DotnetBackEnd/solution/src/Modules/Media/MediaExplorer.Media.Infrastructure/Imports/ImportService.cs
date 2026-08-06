using System.Text;
using System.Text.Json;
using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class ImportService(
    MediaCatalogDbContext dbContext,
    LibraryBootstrapper libraryBootstrapper,
    IObjectStoreFactory objectStoreFactory,
    IOriginalCache originalCache,
    MediaCatalogService catalogService,
    IUploaderIdentityDirectory uploaderIdentityDirectory,
    MediaMetadataWorkSignal metadataWorkSignal,
    ImportWorkSignal importWorkSignal,
    ILogger<ImportService> logger) : IImportService
{
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(15);
    private const int MaximumDiscoveredFiles = 100_000;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<Result<ImportOptionsRes>> GetOptionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var folders = await dbContext.Folders.AsNoTracking()
            .Where(folder => folder.LibraryId == library.Id && folder.TrashedAt == null)
            .Select(folder => new FolderPathEntry(
                folder.Id,
                folder.ParentFolderId,
                folder.Name,
                folder.SystemRole))
            .ToListAsync(cancellationToken);
        var paths = BuildFolderPaths(folders);
        return new ImportOptionsRes
        {
            Destinations = folders
                .Select(folder => new ImportDestinationRes
                {
                    Id = folder.Id,
                    Name = folder.Name,
                    Path = paths.GetValueOrDefault(folder.Id, folder.Name),
                    SystemRole = folder.SystemRole
                })
                .OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public async Task<Result<IReadOnlyList<ImportJobRes>>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var jobs = await dbContext.ImportJobs.AsNoTracking()
            .Where(job => job.UserId == userId)
            .OrderByDescending(job => job.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyList<ImportJobRes>>.Success(await ToResponsesAsync(jobs, cancellationToken));
    }

    public async Task<Result<ImportJobRes>> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.ImportJobs.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId && candidate.UserId == userId, cancellationToken);
        if (job is null) return MediaErrors.NotFound("The requested import job was not found.");
        return (await ToResponsesAsync([job], cancellationToken))[0];
    }

    public async Task<Result<ImportItemResultsRes>> ListItemsAsync(
        Guid userId,
        Guid jobId,
        string category,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalizedCategory = category.Trim().ToLowerInvariant();
        if (normalizedCategory is not ("all" or "imported" or "skipped" or "duplicates" or "name_conflicts" or "warnings" or "failed"))
            return MediaErrors.Validation("Import item category is invalid.");
        if (!await dbContext.ImportJobs.AsNoTracking()
                .AnyAsync(job => job.Id == jobId && job.UserId == userId, cancellationToken))
            return MediaErrors.NotFound("The requested import job was not found.");

        if (!TryDecodeItemCursor(cursor, out var offset))
            return MediaErrors.Validation("Import item cursor is invalid.");

        var query = dbContext.ImportItems.AsNoTracking().Where(item => item.ImportJobId == jobId);
        query = normalizedCategory switch
        {
            "imported" => query.Where(item => item.State == ImportItem.ImportedState),
            "skipped" => query.Where(item => item.State == ImportItem.SkippedState),
            "duplicates" => query.Where(item => item.OutcomeCode == "duplicate_active"
                || item.OutcomeCode == "duplicate_trashed"),
            "name_conflicts" => query.Where(item => item.OutcomeCode == "name_conflict"),
            "warnings" => query.Where(item => item.WarningCodes != null),
            "failed" => query.Where(item => item.State == ImportItem.FailedState),
            _ => query
        };

        var page = await query.OrderBy(item => item.Id).Skip(offset).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        return new ImportItemResultsRes
        {
            Items = page.Select(item => new ImportItemResultRes
            {
                Id = item.Id,
                CatalogFileId = item.CatalogFileId,
                Name = item.Name,
                RelativePath = item.SourceRelativePath,
                State = item.State,
                OutcomeCode = item.OutcomeCode,
                WarningCodes = DeserializeWarnings(item.WarningCodes),
                Error = item.Error
            }).ToList(),
            NextCursor = hasMore ? EncodeItemCursor(offset + page.Count) : null
        };
    }

    public async Task<Result<ImportJobRes>> StartGoogleTakeoutAsync(
        Guid userId,
        CreateGoogleTakeoutImportReq request,
        CancellationToken cancellationToken)
    {
        var policy = request.NameConflictPolicy?.Trim().ToLowerInvariant();
        if (policy is not ("skip" or "keep_both"))
            return MediaErrors.Validation("Name conflict policy must be skip or keep_both.");
        var storedPolicy = StoredConflictPolicy(policy, request.SkipIdenticalFiles);
        var sourceResult = ValidateSourceRoot(request.SourcePath);
        if (sourceResult.IsFailure) return sourceResult.ErrorValue;
        var sourceRoot = sourceResult.SuccessValue;
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var destinationResult = await libraryBootstrapper.EnsureParentAsync(
            library.Id,
            request.DestinationFolderId,
            requireActive: true,
            cancellationToken);
        if (destinationResult.IsFailure) return destinationResult.ErrorValue;
        var destinationPath = await FolderPathAsync(
            library.Id,
            request.DestinationFolderId,
            cancellationToken);

        ScanResult scan;
        try
        {
            scan = await ScanAsync(sourceRoot, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            logger.LogWarning(
                "[media.import] source scan failed exceptionType={ExceptionType}",
                exception.GetType().Name);
            return MediaErrors.Validation("The Takeout directory could not be scanned safely.");
        }
        if (scan.Candidates.Count == 0)
            return MediaErrors.Validation("No importable files were found beneath this Takeout directory.");

        var now = DateTime.UtcNow;
        var job = new ImportJob
        {
            UserId = userId,
            LibraryId = library.Id,
            TargetFolderId = request.DestinationFolderId,
            TargetFolderPath = destinationPath,
            SourcePath = sourceRoot,
            NameConflictPolicy = storedPolicy,
            State = ImportJob.RunningState,
            OrphanSidecarCount = scan.OrphanSidecarCount,
            IgnoredJsonCount = scan.IgnoredJsonCount,
            StartedAt = now,
            CreatedAt = now
        };
        dbContext.ImportJobs.Add(job);
        dbContext.ImportItems.AddRange(scan.Candidates.Select(candidate => new ImportItem
        {
            ImportJobId = job.Id,
            SourceRelativePath = candidate.RelativePath,
            SidecarRelativePath = candidate.SidecarRelativePath,
            Name = candidate.Name,
            ExpectedSizeBytes = candidate.SizeBytes,
            ImportedCapturedAt = candidate.Metadata?.CapturedAt,
            ImportedDescription = candidate.Metadata?.Description,
            ImportedLatitude = candidate.Metadata?.Latitude,
            ImportedLongitude = candidate.Metadata?.Longitude,
            ImportedAltitudeMeters = candidate.Metadata?.AltitudeMeters,
            WarningCodes = candidate.Warnings.Count == 0 ? null : JsonSerializer.Serialize(candidate.Warnings)
        }));
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "[media.import] job created jobId={JobId} discoveredCount={DiscoveredCount} orphanSidecarCount={OrphanSidecarCount} ignoredJsonCount={IgnoredJsonCount}",
            job.Id,
            scan.Candidates.Count,
            scan.OrphanSidecarCount,
            scan.IgnoredJsonCount);
        importWorkSignal.Signal();
        return (await ToResponsesAsync([job], cancellationToken))[0];
    }

    public async Task<Result<ImportJobRes>> RetryFailedAsync(Guid userId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await dbContext.ImportJobs.SingleOrDefaultAsync(
            candidate => candidate.Id == jobId && candidate.UserId == userId,
            cancellationToken);
        if (job is null) return MediaErrors.NotFound("The requested import job was not found.");
        var failed = await dbContext.ImportItems
            .Where(item => item.ImportJobId == job.Id && item.State == ImportItem.FailedState)
            .ToListAsync(cancellationToken);
        if (failed.Count == 0) return MediaErrors.Conflict("This import has no failed items to retry.");
        foreach (var item in failed)
        {
            item.State = ImportItem.PendingState;
            item.ProcessingStartedAt = null;
            item.CompletedAt = null;
            item.OutcomeCode = null;
            item.Error = null;
        }
        job.State = ImportJob.RunningState;
        job.CompletedAt = null;
        job.Error = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        importWorkSignal.Signal();
        return (await ToResponsesAsync([job], cancellationToken))[0];
    }

    internal async Task<int> ProcessPendingBatchAsync(int limit, CancellationToken cancellationToken)
    {
        var processed = 0;
        for (var index = 0; index < limit; index++)
        {
            var staleBefore = DateTime.UtcNow - ProcessingLease;
            var item = await dbContext.ImportItems
                .Where(candidate => candidate.State == ImportItem.PendingState
                    || (candidate.State == ImportItem.ProcessingState && candidate.ProcessingStartedAt < staleBefore))
                .Join(
                    dbContext.ImportJobs.Where(job => job.State == ImportJob.RunningState),
                    candidate => candidate.ImportJobId,
                    job => job.Id,
                    (candidate, _) => candidate)
                .OrderBy(candidate => candidate.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (item is null) break;

            item.State = ImportItem.ProcessingState;
            item.ProcessingStartedAt = DateTime.UtcNow;
            item.AttemptCount++;
            item.Error = null;
            await dbContext.SaveChangesAsync(cancellationToken);

            try
            {
                await ProcessItemAsync(item, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ResetInterruptedItemAsync(item.Id);
                throw;
            }
            catch (Exception exception)
            {
                var outcome = ClassifyFailure(exception);
                await MarkFailedAsync(item.Id, outcome, cancellationToken);
                logger.LogError(
                    "[media.import] item failed jobId={JobId} importItemId={ImportItemId} attempt={Attempt} outcome={Outcome} exceptionType={ExceptionType}",
                    item.ImportJobId,
                    item.Id,
                    item.AttemptCount,
                    outcome,
                    exception.GetType().Name);
            }
            processed++;
        }
        await FinalizeCompletedJobsAsync(cancellationToken);
        return processed;
    }

    private async Task ProcessItemAsync(ImportItem item, CancellationToken cancellationToken)
    {
        var job = await dbContext.ImportJobs.SingleAsync(candidate => candidate.Id == item.ImportJobId, cancellationToken);
        var sourceRootResult = ValidateSourceRoot(job.SourcePath);
        if (sourceRootResult.IsFailure) throw new InvalidDataException("source_unavailable");
        var sourcePath = ResolveSafeItemPath(sourceRootResult.SuccessValue, item.SourceRelativePath);
        var fileInfo = new FileInfo(sourcePath);
        if (!fileInfo.Exists || fileInfo.Length != item.ExpectedSizeBytes)
            throw new InvalidDataException("source_changed");

        await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var sha256 = await UploadHash.Sha256Async(stream, cancellationToken);
        if (SkipsIdenticalFiles(job.NameConflictPolicy)
            && await FindByteDuplicateAsync(job.LibraryId, sha256, cancellationToken) is { } earlyDuplicate)
        {
            item.CatalogFileId = earlyDuplicate.Id;
            CompleteItem(item, ImportItem.SkippedState, earlyDuplicate.TrashedAt.HasValue ? "duplicate_trashed" : "duplicate_active");
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var targetFolderId = job.TargetFolderId ?? await libraryBootstrapper.SystemFolderIdAsync(
            job.LibraryId,
            LibraryBootstrapper.MediaFolderRole,
            cancellationToken);
        var destinationIsActive = await dbContext.Folders.AsNoTracking().AnyAsync(
            folder => folder.Id == targetFolderId
                && folder.LibraryId == job.LibraryId
                && folder.TrashedAt == null,
            cancellationToken);
        if (!destinationIsActive) throw new InvalidDataException("destination_unavailable");
        if (NameConflictPolicy(job.NameConflictPolicy) == "skip"
            && await dbContext.Files.AsNoTracking().AnyAsync(file => file.LibraryId == job.LibraryId
                && file.ParentFolderId == targetFolderId
                && file.TrashedAt == null
                && file.Name == item.Name,
                cancellationToken))
        {
            CompleteItem(item, ImportItem.SkippedState, "name_conflict");
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }
        var uploaderEmail = await uploaderIdentityDirectory.GetEmailAsync(job.UserId, cancellationToken);

        await MediaCatalogService.OriginalMutationGate.WaitAsync(cancellationToken);
        try
        {
            if (SkipsIdenticalFiles(job.NameConflictPolicy)
                && await FindByteDuplicateAsync(job.LibraryId, sha256, cancellationToken) is { } gatedDuplicate)
            {
                item.CatalogFileId = gatedDuplicate.Id;
                CompleteItem(item, ImportItem.SkippedState, gatedDuplicate.TrashedAt.HasValue ? "duplicate_trashed" : "duplicate_active");
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }

            var now = DateTime.UtcNow;
            var blobExists = await dbContext.Blobs.AsNoTracking().AnyAsync(blob => blob.Sha256 == sha256, cancellationToken);
            stream.Position = 0;
            using var objectStore = objectStoreFactory.Create();
            try
            {
                await objectStore.PutOriginalIfMissingAsync(
                    sha256,
                    stream,
                    item.ExpectedSizeBytes,
                    "application/octet-stream",
                    item.Name,
                    job.UserId,
                    uploaderEmail,
                    now,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("storage_failed", exception);
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var blob = await dbContext.Blobs.SingleOrDefaultAsync(candidate => candidate.Sha256 == sha256, cancellationToken);
            if (blob is null)
            {
                blob = new Blob
                {
                    Sha256 = sha256,
                    ObjectKey = S3ObjectStore.BlobKey(sha256),
                    SizeBytes = item.ExpectedSizeBytes,
                    CreatedAt = now
                };
                dbContext.Blobs.Add(blob);
            }
            else if (blob.LifecycleState == Blob.PendingPermanentDeletionLifecycleState)
            {
                throw new InvalidOperationException("The stored original is pending permanent deletion.");
            }

            var library = await dbContext.Libraries.SingleAsync(candidate => candidate.Id == job.LibraryId, cancellationToken);
            var effectiveCapturedAt = item.ImportedCapturedAt ?? now;
            var version = new FileVersion
            {
                BlobSha256 = sha256,
                SizeBytes = item.ExpectedSizeBytes,
                MimeType = "application/octet-stream",
                DeclaredMimeType = "application/octet-stream",
                OriginalName = item.Name,
                UploaderUserId = job.UserId,
                CreatedAt = now
            };
            var file = new CatalogFile
            {
                LibraryId = library.Id,
                ParentFolderId = targetFolderId,
                Name = item.Name,
                MimeType = "application/octet-stream",
                SizeBytes = item.ExpectedSizeBytes,
                Sequence = ++library.NextItemSequence,
                CreatedAt = now,
                ModifiedAt = now,
                CapturedAt = effectiveCapturedAt,
                CaptureDateSource = item.ImportedCapturedAt.HasValue ? "imported_metadata" : "uploaded_at",
                ImportedCapturedAt = item.ImportedCapturedAt,
                Description = item.ImportedDescription,
                Latitude = item.ImportedLatitude,
                Longitude = item.ImportedLongitude,
                AltitudeMeters = item.ImportedAltitudeMeters,
                MetadataState = "pending",
                CurrentVersionId = version.Id
            };
            version.FileId = file.Id;
            dbContext.Files.Add(file);
            dbContext.FileVersions.Add(version);
            dbContext.FileVersionMetadata.Add(new FileVersionMetadata
            {
                FileVersionId = version.Id,
                State = "pending",
                ImportedCapturedAt = item.ImportedCapturedAt,
                ImportedDescription = item.ImportedDescription,
                ImportedLatitude = item.ImportedLatitude,
                ImportedLongitude = item.ImportedLongitude,
                ImportedAltitudeMeters = item.ImportedAltitudeMeters,
                EffectiveCapturedAt = effectiveCapturedAt,
                CaptureDateSource = item.ImportedCapturedAt.HasValue ? "imported_metadata" : "uploaded_at",
                RecoveryManifestState = "pending"
            });
            item.CatalogFileId = file.Id;
            CompleteItem(item, ImportItem.ImportedState, blobExists ? "imported_reused_original" : "imported_new_original");
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            catalogService.InvalidateStorageSummary(job.UserId);

            try
            {
                await originalCache.EnsureFromFileAsync(sha256, sourcePath, item.ExpectedSizeBytes, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "[media.import] original cache seed deferred jobId={JobId} importItemId={ImportItemId} exceptionType={ExceptionType}",
                    job.Id,
                    item.Id,
                    exception.GetType().Name);
            }
            metadataWorkSignal.Signal();
            logger.LogInformation(
                "[media.import] item completed jobId={JobId} importItemId={ImportItemId} itemId={ItemId} sizeBytes={SizeBytes} outcome={Outcome}",
                job.Id,
                item.Id,
                file.Id,
                item.ExpectedSizeBytes,
                item.OutcomeCode);
        }
        finally
        {
            MediaCatalogService.OriginalMutationGate.Release();
        }
    }

    private static async Task<ScanResult> ScanAsync(string sourceRoot, CancellationToken cancellationToken)
    {
        var files = EnumerateSafeFiles(sourceRoot).OrderBy(path => path, PathComparer).ToList();
        if (files.Count > MaximumDiscoveredFiles)
            throw new InvalidDataException("source_too_large");

        var sidecarPaths = files
            .Where(IsSupplementalMetadata)
            .ToHashSet(PathComparer);
        var ignoredJsonCount = files.Count(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !sidecarPaths.Contains(path));
        var parsedSidecars = new Dictionary<string, NormalizedImportMetadata?>(PathComparer);
        foreach (var path in sidecarPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                parsedSidecars[path] = await GoogleTakeoutSidecarReader.ReadAsync(path, cancellationToken);
            }
            catch (JsonException)
            {
                parsedSidecars[path] = null;
            }
        }

        var usedSidecars = new HashSet<string>(PathComparer);
        var candidates = new List<ImportCandidate>();
        foreach (var mediaPath in files.Where(path => !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var warnings = new List<string>();
            string? sidecarPath = null;
            NormalizedImportMetadata? metadata = null;
            var exactPath = $"{mediaPath}.supplemental-metadata.json";
            if (parsedSidecars.TryGetValue(exactPath, out metadata))
            {
                sidecarPath = exactPath;
                usedSidecars.Add(exactPath);
                if (metadata is null)
                {
                    warnings.Add("sidecar_invalid");
                }
                else if (!string.IsNullOrWhiteSpace(metadata.Title)
                    && !string.Equals(metadata.Title, Path.GetFileName(mediaPath), StringComparison.Ordinal))
                {
                    warnings.Add("sidecar_title_mismatch");
                }
            }
            else
            {
                var titleMatches = parsedSidecars
                    .Where(pair => pair.Value is not null
                        && PathComparer.Equals(Path.GetDirectoryName(pair.Key), Path.GetDirectoryName(mediaPath))
                        && string.Equals(pair.Value.Title, Path.GetFileName(mediaPath), StringComparison.Ordinal))
                    .ToList();
                if (titleMatches.Count == 1)
                {
                    sidecarPath = titleMatches[0].Key;
                    metadata = titleMatches[0].Value;
                    usedSidecars.Add(sidecarPath);
                }
                else
                {
                    warnings.Add(titleMatches.Count > 1 ? "sidecar_ambiguous" : "sidecar_missing");
                }
            }
            if (metadata is not null) warnings.AddRange(metadata.Warnings);
            candidates.Add(new ImportCandidate(
                RelativePath(sourceRoot, mediaPath),
                sidecarPath is null ? null : RelativePath(sourceRoot, sidecarPath),
                Path.GetFileName(mediaPath),
                new FileInfo(mediaPath).Length,
                metadata,
                warnings.Distinct(StringComparer.Ordinal).ToArray()));
        }
        return new ScanResult(candidates, sidecarPaths.Count - usedSidecars.Count, ignoredJsonCount);
    }

    private static Result<string> ValidateSourceRoot(string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return MediaErrors.Validation("Choose an extracted Google Photos Takeout directory.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return MediaErrors.Validation("The import source path is invalid.");
        }
        if (!Directory.Exists(fullPath))
            return MediaErrors.Validation("The import source directory does not exist.");
        if (new DirectoryInfo(fullPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            return MediaErrors.Validation("Symbolic-link import sources are not allowed.");
        return fullPath;
    }

    private static IEnumerable<string> EnumerateSafeFiles(string sourceRoot)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(sourceRoot));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("source_symlink");
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo file)
                {
                    yield return file.FullName;
                }
            }
        }
    }

    private static string ResolveSafeItemPath(string sourceRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(sourceRoot, fullPath))
            throw new InvalidDataException("source_outside_root");
        var current = new FileInfo(fullPath);
        for (FileSystemInfo? entry = current; entry is not null && !PathComparer.Equals(entry.FullName, sourceRoot); entry = entry switch
        {
            FileInfo file => file.Directory,
            DirectoryInfo directory => directory.Parent,
            _ => null
        })
        {
            if (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("source_symlink");
        }
        return fullPath;
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsSupplementalMetadata(string path) =>
        path.EndsWith(".supplemental-metadata.json", StringComparison.OrdinalIgnoreCase);

    private static void CompleteItem(ImportItem item, string state, string outcome)
    {
        item.State = state;
        item.OutcomeCode = outcome;
        item.ProcessingStartedAt = null;
        item.CompletedAt = DateTime.UtcNow;
        item.Error = null;
    }

    private async Task ResetInterruptedItemAsync(Guid itemId)
    {
        dbContext.ChangeTracker.Clear();
        var item = await dbContext.ImportItems.SingleAsync(candidate => candidate.Id == itemId, CancellationToken.None);
        if (item.State == ImportItem.ProcessingState)
        {
            item.State = ImportItem.PendingState;
            item.ProcessingStartedAt = null;
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task MarkFailedAsync(Guid itemId, string outcome, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var item = await dbContext.ImportItems.SingleAsync(candidate => candidate.Id == itemId, cancellationToken);
        item.State = ImportItem.FailedState;
        item.OutcomeCode = outcome;
        item.Error = "This item could not be imported. Review the source and retry.";
        item.ProcessingStartedAt = null;
        item.CompletedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task FinalizeCompletedJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = await dbContext.ImportJobs
            .Where(job => job.State == ImportJob.RunningState)
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            var hasWork = await dbContext.ImportItems.AnyAsync(
                item => item.ImportJobId == job.Id
                    && (item.State == ImportItem.PendingState || item.State == ImportItem.ProcessingState),
                cancellationToken);
            if (hasWork) continue;
            var hasFailures = await dbContext.ImportItems.AnyAsync(
                item => item.ImportJobId == job.Id && item.State == ImportItem.FailedState,
                cancellationToken);
            job.State = hasFailures ? ImportJob.CompletedWithErrorsState : ImportJob.CompletedState;
            job.CompletedAt = DateTime.UtcNow;
            logger.LogInformation("[media.import] job completed jobId={JobId} state={State}", job.Id, job.State);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ImportJobRes>> ToResponsesAsync(
        List<ImportJob> jobs,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0) return [];
        var libraryIds = jobs.Select(job => job.LibraryId).Distinct().ToArray();
        var folders = await dbContext.Folders.AsNoTracking()
            .Where(folder => libraryIds.Contains(folder.LibraryId))
            .Select(folder => new JobFolderEntry(
                folder.Id,
                folder.LibraryId,
                folder.ParentFolderId,
                folder.Name,
                folder.SystemRole))
            .ToListAsync(cancellationToken);
        var folderPaths = BuildFolderPaths(folders.Select(folder =>
            new FolderPathEntry(folder.Id, folder.ParentFolderId, folder.Name, folder.SystemRole)));
        var mediaFolders = folders
            .Where(folder => folder.SystemRole == LibraryBootstrapper.MediaFolderRole)
            .ToDictionary(folder => folder.LibraryId, folder => folder.Id);
        var jobIds = jobs.Select(job => job.Id).ToArray();
        var items = await dbContext.ImportItems.AsNoTracking()
            .Where(item => jobIds.Contains(item.ImportJobId))
            .Select(item => new { item.ImportJobId, item.State, item.OutcomeCode, item.WarningCodes })
            .ToListAsync(cancellationToken);
        var byJob = items.GroupBy(item => item.ImportJobId).ToDictionary(group => group.Key, group => group.ToList());
        return jobs.Select(job =>
        {
            var jobItems = byJob.GetValueOrDefault(job.Id) ?? [];
            var destinationFolderId = job.TargetFolderId ?? mediaFolders.GetValueOrDefault(job.LibraryId);
            return new ImportJobRes
            {
                Id = job.Id,
                DestinationFolderId = destinationFolderId == Guid.Empty ? null : destinationFolderId,
                DestinationPath = job.TargetFolderPath
                    ?? (destinationFolderId != Guid.Empty
                        ? folderPaths.GetValueOrDefault(destinationFolderId, "Unavailable folder")
                        : "Unavailable folder"),
                SourceKind = job.SourceKind,
                NameConflictPolicy = NameConflictPolicy(job.NameConflictPolicy),
                SkipIdenticalFiles = SkipsIdenticalFiles(job.NameConflictPolicy),
                State = job.State,
                DiscoveredCount = jobItems.Count,
                PendingCount = jobItems.Count(item => item.State == ImportItem.PendingState),
                ProcessingCount = jobItems.Count(item => item.State == ImportItem.ProcessingState),
                ImportedCount = jobItems.Count(item => item.State == ImportItem.ImportedState),
                DuplicateCount = jobItems.Count(item => item.OutcomeCode is "duplicate_active" or "duplicate_trashed"),
                NameConflictCount = jobItems.Count(item => item.OutcomeCode == "name_conflict"),
                FailedCount = jobItems.Count(item => item.State == ImportItem.FailedState),
                WarningCount = jobItems.Count(item => item.WarningCodes is not null),
                OrphanSidecarCount = job.OrphanSidecarCount,
                IgnoredJsonCount = job.IgnoredJsonCount,
                Outcomes = jobItems
                    .Where(item => item.OutcomeCode is not null)
                    .GroupBy(item => item.OutcomeCode!, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                Error = job.Error,
                CreatedAt = job.CreatedAt,
                StartedAt = job.StartedAt,
                CompletedAt = job.CompletedAt
            };
        }).ToList();
    }

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        InvalidDataException { Message: "source_changed" } => "source_changed",
        InvalidDataException { Message: "destination_unavailable" } => "destination_unavailable",
        InvalidDataException { Message: "storage_failed" } => "storage_failed",
        InvalidDataException => "read_failed",
        IOException or UnauthorizedAccessException => "read_failed",
        _ when exception.GetType().Namespace?.StartsWith("Amazon", StringComparison.Ordinal) == true => "storage_failed",
        _ => "catalog_failed"
    };

    private async Task<ByteDuplicate?> FindByteDuplicateAsync(
        Guid libraryId,
        string sha256,
        CancellationToken cancellationToken) =>
        await (
            from version in dbContext.FileVersions.AsNoTracking()
            join file in dbContext.Files.AsNoTracking() on version.FileId equals file.Id
            where file.LibraryId == libraryId && version.BlobSha256 == sha256
            select new ByteDuplicate(file.Id, file.TrashedAt))
        .FirstOrDefaultAsync(cancellationToken);

    private static string StoredConflictPolicy(string nameConflictPolicy, bool skipIdenticalFiles) =>
        (nameConflictPolicy, skipIdenticalFiles) switch
        {
            ("skip", true) => "skip",
            ("keep_both", true) => "keep_both",
            ("skip", false) => "skip_name_only",
            _ => "keep_all"
        };

    private static string NameConflictPolicy(string storedPolicy) =>
        storedPolicy is "skip" or "skip_name_only" ? "skip" : "keep_both";

    private static bool SkipsIdenticalFiles(string storedPolicy) =>
        storedPolicy is not ("skip_name_only" or "keep_all");

    private static string[] DeserializeWarnings(string? warningCodes)
    {
        if (string.IsNullOrWhiteSpace(warningCodes)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(warningCodes) ?? [];
        }
        catch (JsonException)
        {
            return ["warning_details_unavailable"];
        }
    }

    private static string EncodeItemCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));

    private static bool TryDecodeItemCursor(string? cursor, out int offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(value, out offset) && offset >= 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Dictionary<Guid, string> BuildFolderPaths(IEnumerable<FolderPathEntry> entries)
    {
        var folders = entries.ToDictionary(folder => folder.Id);
        var paths = new Dictionary<Guid, string>();
        foreach (var folder in folders.Values) BuildPath(folder.Id, new HashSet<Guid>());
        return paths;

        string BuildPath(Guid folderId, HashSet<Guid> ancestors)
        {
            if (paths.TryGetValue(folderId, out var existing)) return existing;
            if (!folders.TryGetValue(folderId, out var folder) || !ancestors.Add(folderId)) return string.Empty;
            var parentPath = folder.ParentFolderId.HasValue
                ? BuildPath(folder.ParentFolderId.Value, ancestors)
                : string.Empty;
            ancestors.Remove(folderId);
            var path = string.IsNullOrEmpty(parentPath) ? folder.Name : $"{parentPath} / {folder.Name}";
            paths[folderId] = path;
            return path;
        }
    }

    private async Task<string> FolderPathAsync(
        Guid libraryId,
        Guid folderId,
        CancellationToken cancellationToken)
    {
        var folders = await dbContext.Folders.AsNoTracking()
            .Where(folder => folder.LibraryId == libraryId && folder.TrashedAt == null)
            .Select(folder => new FolderPathEntry(
                folder.Id,
                folder.ParentFolderId,
                folder.Name,
                folder.SystemRole))
            .ToListAsync(cancellationToken);
        return BuildFolderPaths(folders).GetValueOrDefault(folderId, "Unavailable folder");
    }

    private sealed record ImportCandidate(
        string RelativePath,
        string? SidecarRelativePath,
        string Name,
        long SizeBytes,
        NormalizedImportMetadata? Metadata,
        IReadOnlyList<string> Warnings);

    private sealed record ScanResult(
        IReadOnlyList<ImportCandidate> Candidates,
        int OrphanSidecarCount,
        int IgnoredJsonCount);

    private sealed record FolderPathEntry(
        Guid Id,
        Guid? ParentFolderId,
        string Name,
        string? SystemRole);

    private sealed record JobFolderEntry(
        Guid Id,
        Guid LibraryId,
        Guid? ParentFolderId,
        string Name,
        string? SystemRole);

    private sealed record ByteDuplicate(Guid Id, DateTime? TrashedAt);

}
