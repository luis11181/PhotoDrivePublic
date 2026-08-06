using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class CatalogBatchService(
    MediaCatalogDbContext dbContext,
    LibraryBootstrapper libraryBootstrapper,
    ICatalogCommandService commands) : ICatalogBatchService
{
    private const int MaximumItems = 300;

    public async Task<Result<BatchItemsRes>> SetCaptureDateAsync(
        Guid userId,
        SetCaptureDateReq request,
        CancellationToken cancellationToken)
    {
        var itemIds = request.ItemIds ?? [];
        if (itemIds.Length is < 1 or > MaximumItems)
            return MediaErrors.Validation($"Select between 1 and {MaximumItems} items.");
        if (itemIds.Distinct().Count() != itemIds.Length)
            return MediaErrors.Validation("Capture-date item IDs must be unique.");
        if (request.Reset == request.CapturedOn.HasValue)
            return MediaErrors.Validation("Provide one capture date or choose to restore the detected date.");

        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var ids = itemIds.ToHashSet();
        var files = await dbContext.Files
            .Where(file => ids.Contains(file.Id) && file.LibraryId == library.Id)
            .ToDictionaryAsync(file => file.Id, cancellationToken);
        var versionIds = files.Values.Select(file => file.CurrentVersionId).ToArray();
        var versions = await dbContext.FileVersions.AsNoTracking()
            .Where(version => versionIds.Contains(version.Id))
            .ToDictionaryAsync(version => version.Id, cancellationToken);
        var metadata = await dbContext.FileVersionMetadata
            .Where(item => versionIds.Contains(item.FileVersionId))
            .ToDictionaryAsync(item => item.FileVersionId, cancellationToken);
        var results = new List<BatchItemResultRes>(itemIds.Length);
        var now = DateTime.UtcNow;
        var correctedAt = request.CapturedOn?.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var itemId in itemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(itemId, out var file))
            {
                results.Add(Outcome(itemId, "failed", MediaErrors.NotFoundCode, "The requested item was not found in your library."));
                continue;
            }
            if (file.TrashedAt.HasValue)
            {
                results.Add(Outcome(itemId, "failed", MediaErrors.ConflictCode, "Restore this item before correcting its capture date."));
                continue;
            }

            var unchanged = request.Reset ? !file.UserCapturedAt.HasValue : file.UserCapturedAt == correctedAt;
            if (unchanged)
            {
                results.Add(Outcome(itemId, "unchanged"));
                continue;
            }

            file.UserCapturedAt = correctedAt;
            var version = versions.GetValueOrDefault(file.CurrentVersionId);
            var currentMetadata = version is not null ? metadata.GetValueOrDefault(version.Id) : null;
            var effective = request.Reset
                ? ResolveDetectedCaptureDate(currentMetadata, version?.CreatedAt ?? file.CreatedAt)
                : (CapturedAt: correctedAt!.Value, Source: "user");
            file.CapturedAt = effective.CapturedAt;
            file.CaptureDateSource = effective.Source;
            file.ModifiedAt = now;
            if (currentMetadata is not null)
            {
                currentMetadata.EffectiveCapturedAt = effective.CapturedAt;
                currentMetadata.CaptureDateSource = effective.Source;
            }
            results.Add(Outcome(itemId, "succeeded"));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Summarize(itemIds.Length, results);
    }

    public async Task<Result<BatchItemsRes>> ExecuteAsync(Guid userId, BatchItemsReq request, CancellationToken cancellationToken)
    {
        var action = request.Action?.Trim().ToLowerInvariant();
        var itemIds = request.ItemIds ?? [];
        if (action is not ("favorite" or "move" or "trash" or "restore"))
            return MediaErrors.Validation("Batch action must be favorite, move, trash, or restore.");
        if (itemIds.Length is < 1 or > MaximumItems)
            return MediaErrors.Validation($"Select between 1 and {MaximumItems} items.");
        if (itemIds.Distinct().Count() != itemIds.Length)
            return MediaErrors.Validation("Batch item IDs must be unique.");
        if (action == "favorite" && !request.Favorite.HasValue)
            return MediaErrors.Validation("Favorite batches require the desired favorite value.");

        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        Guid? destinationId = null;
        if (action == "move")
        {
            destinationId = request.ParentId ?? await libraryBootstrapper.SystemFolderIdAsync(
                library.Id,
                LibraryBootstrapper.DocumentsFolderRole,
                cancellationToken);
            var destination = await libraryBootstrapper.EnsureParentAsync(library.Id, destinationId, requireActive: true, cancellationToken);
            if (destination.IsFailure) return destination.ErrorValue;
        }

        var selectedIds = itemIds.ToHashSet();
        var selectedFolders = await dbContext.Folders.AsNoTracking()
            .Where(folder => selectedIds.Contains(folder.Id) && folder.LibraryId == library.Id)
            .Select(folder => new BatchItemState(folder.Id, folder.ParentFolderId, folder.TrashedAt, folder.TrashRootId))
            .ToListAsync(cancellationToken);
        var selectedFiles = await dbContext.Files.AsNoTracking()
            .Where(file => selectedIds.Contains(file.Id) && file.LibraryId == library.Id)
            .Select(file => new BatchItemState(file.Id, file.ParentFolderId, file.TrashedAt, file.TrashRootId))
            .ToListAsync(cancellationToken);
        var covered = action is "move" or "trash"
            ? await CoveredBySelectedFolderAsync(
                library.Id,
                selectedFolders.Select(folder => folder.Id).ToHashSet(),
                selectedFiles,
                cancellationToken)
            : [];
        if (action == "restore")
        {
            foreach (var group in selectedFolders.Concat(selectedFiles)
                .Where(item => item.TrashedAt.HasValue)
                .GroupBy(file => file.TrashRootId ?? file.Id))
            {
                foreach (var duplicate in group.Skip(1)) covered.Add(duplicate.Id);
            }
        }

        var foldersById = selectedFolders.ToDictionary(item => item.Id);
        var filesById = selectedFiles.ToDictionary(item => item.Id);
        var results = new List<BatchItemResultRes>(itemIds.Length);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var itemId in itemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (covered.Contains(itemId))
            {
                results.Add(Outcome(itemId, "skipped", message: "Covered by another selected folder or Trash root."));
                continue;
            }

            if (TryUnchanged(action!, destinationId, itemId, foldersById, filesById))
            {
                results.Add(Outcome(itemId, "unchanged"));
                continue;
            }

            Result<MediaItemRes> result = action switch
            {
                "favorite" => await commands.SetFavoriteAsync(userId, itemId, new SetFavoriteReq { Favorite = request.Favorite!.Value }, cancellationToken),
                "move" => await commands.MoveItemAsync(userId, itemId, new MoveItemReq { ParentId = destinationId }, cancellationToken),
                "trash" => await commands.TrashItemAsync(userId, itemId, cancellationToken),
                _ => await commands.RestoreItemAsync(userId, itemId, cancellationToken)
            };
            results.Add(result.IsSuccess
                ? Outcome(itemId, "succeeded")
                : Outcome(itemId, "failed", result.ErrorValue.ErrorCode, result.ErrorValue.Message));
        }
        await transaction.CommitAsync(cancellationToken);
        return Summarize(itemIds.Length, results);
    }

    private static bool TryUnchanged(
        string action,
        Guid? destinationId,
        Guid itemId,
        IReadOnlyDictionary<Guid, BatchItemState> folders,
        IReadOnlyDictionary<Guid, BatchItemState> files)
    {
        var item = folders.GetValueOrDefault(itemId) ?? files.GetValueOrDefault(itemId);
        if (item is null) return false;
        return action switch
        {
            "move" => item.TrashedAt is null && item.ParentFolderId == destinationId,
            "trash" => item.TrashedAt is not null,
            "restore" => item.TrashedAt is null,
            _ => false
        };
    }

    private async Task<HashSet<Guid>> CoveredBySelectedFolderAsync(
        Guid libraryId,
        HashSet<Guid> selectedFolderIds,
        IReadOnlyCollection<BatchItemState> selectedFiles,
        CancellationToken cancellationToken)
    {
        if (selectedFolderIds.Count == 0) return [];
        var parents = await dbContext.Folders.AsNoTracking()
            .Where(folder => folder.LibraryId == libraryId)
            .Select(folder => new { folder.Id, folder.ParentFolderId })
            .ToDictionaryAsync(folder => folder.Id, folder => folder.ParentFolderId, cancellationToken);
        var covered = new HashSet<Guid>();
        foreach (var folderId in selectedFolderIds)
        {
            var current = parents.GetValueOrDefault(folderId);
            while (current.HasValue)
            {
                if (selectedFolderIds.Contains(current.Value))
                {
                    covered.Add(folderId);
                    break;
                }
                current = parents.GetValueOrDefault(current.Value);
            }
        }
        foreach (var file in selectedFiles)
        {
            var current = file.ParentFolderId;
            while (current.HasValue)
            {
                if (selectedFolderIds.Contains(current.Value))
                {
                    covered.Add(file.Id);
                    break;
                }
                current = parents.GetValueOrDefault(current.Value);
            }
        }
        return covered;
    }

    internal static BatchItemResultRes Outcome(Guid itemId, string status, string? errorCode = null, string? message = null) =>
        new() { ItemId = itemId, Status = status, ErrorCode = errorCode, Message = message };

    internal static BatchItemsRes Summarize(int requestedCount, IReadOnlyList<BatchItemResultRes> results) => new()
    {
        RequestedCount = requestedCount,
        SucceededCount = results.Count(item => item.Status == "succeeded"),
        UnchangedCount = results.Count(item => item.Status == "unchanged"),
        SkippedCount = results.Count(item => item.Status == "skipped"),
        FailedCount = results.Count(item => item.Status == "failed"),
        Results = results
    };

    private static (DateTime CapturedAt, string Source) ResolveDetectedCaptureDate(
        FileVersionMetadata? metadata,
        DateTime uploadedAt)
    {
        if (metadata?.ImportedCapturedAt is { } imported) return (imported, "imported_metadata");
        if (metadata?.ExifDateTimeOriginal is { } original) return (original, "exif_datetime_original");
        if (metadata?.ExifDateTimeDigitized is { } digitized) return (digitized, "exif_datetime_digitized");
        if (metadata?.EmbeddedCreatedAt is { } embedded) return (embedded, "embedded_creation_time");
        if (metadata?.FilenameCapturedAt is { } filename) return (filename, "filename");
        return (uploadedAt, "uploaded_at");
    }

    private sealed record BatchItemState(Guid Id, Guid? ParentFolderId, DateTime? TrashedAt, Guid? TrashRootId);
}
