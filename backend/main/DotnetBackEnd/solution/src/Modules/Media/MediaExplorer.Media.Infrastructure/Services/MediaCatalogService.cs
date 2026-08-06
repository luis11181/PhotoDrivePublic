using System.Text;
using System.Text.Json;
using System.Data;
using System.IO.Compression;
using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class MediaCatalogService(MediaCatalogDbContext dbContext, LibraryBootstrapper libraryBootstrapper, CatalogAuthorization authorization, IObjectStoreFactory objectStoreFactory, IOriginalCache originalCache, MediaThumbnailCache thumbnailCache, FileMetadataProcessor metadataProcessor, MediaMetadataWorkSignal metadataWorkSignal, IUploaderIdentityDirectory uploaderIdentityDirectory, IConfiguration configuration, IMemoryCache storageSummaryCache, ILogger<MediaCatalogService> logger) : ICatalogQueryService, ICatalogCommandService, IUploadService, IMediaDeliveryService
{
    internal static readonly SemaphoreSlim OriginalMutationGate = new(1, 1);
    private const int ChunkSizeBytes = 5 * 1024 * 1024;
    private readonly string _stagingDirectory = Path.GetFullPath(configuration["MediaStorage:LocalDisk:StagingDirectory"] ?? "media-explorer.staging");
    private const string StorageSummaryCacheKeyPrefix = "media.storage-summary:";

    public Task<Result<BrowseItemsRes>> BrowsePhotosAsync(Guid userId, string scope, Guid? folderId, string? cursor, int limit, string type, bool favorite, string sort, int? year, string? captureDate, CancellationToken cancellationToken) =>
        BrowsePhotosAsync(userId, scope, folderId, cursor, limit, type, favorite, sort, year, captureDate, null, null, cancellationToken);

    public Task<Result<PhotoTimelineYearsRes>> BrowsePhotoYearsAsync(Guid userId, string scope, Guid? folderId, string type, bool favorite, CancellationToken cancellationToken) =>
        BrowsePhotoYearsAsync(userId, scope, folderId, type, favorite, null, cancellationToken);

    public async Task<Result<UploadNameConflictsRes>> CheckNameConflictsAsync(Guid userId, CheckUploadNameConflictsReq request, CancellationToken cancellationToken)
    {
        if (request.Names.Count > 1_000) return MediaErrors.Validation("Check no more than 1,000 upload names at a time.");
        if (request.UseDefaultMediaFolder && request.UseDefaultDocumentsFolder)
        {
            return MediaErrors.Validation("Choose only one default upload folder.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requestedName in request.Names)
        {
            var nameResult = CatalogItemPolicy.NormalizeName(requestedName, "File");
            if (nameResult.IsFailure) return nameResult.ErrorValue;
            names.Add(nameResult.SuccessValue);
        }

        if (names.Count == 0) return new UploadNameConflictsRes();

        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var parentId = await ResolveUploadParentIdAsync(
            library,
            request.ParentId,
            request.UseDefaultMediaFolder,
            request.UseDefaultDocumentsFolder,
            cancellationToken);
        if (parentId.IsFailure) return parentId.ErrorValue;

        var conflictNames = await dbContext.Files.AsNoTracking()
            .Where(file => file.LibraryId == library.Id
                && file.ParentFolderId == parentId.SuccessValue
                && file.TrashedAt == null
                && names.Contains(file.Name))
            .Select(file => file.Name)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(cancellationToken);

        return new UploadNameConflictsRes { Names = conflictNames };
    }

    public async Task<Result<BrowseItemsRes>> BrowseTrashAsync(Guid userId, string? cursor, int limit, string type, string sort, CancellationToken cancellationToken)
    {
        var normalizedSort = NormalizeItemSort(sort);
        var normalizedType = NormalizeFileType(type);
        if (normalizedSort is null) return MediaErrors.Validation("Item sort must be name_asc, name_desc, modified_desc, modified_asc, size_desc, or size_asc.");
        if (normalizedType is null) return MediaErrors.Validation("File type must be all, folder, document, pdf, spreadsheet, image, or video.");
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var offset = DecodeOffsetCursor(cursor, normalizedSort);
        var folders = dbContext.Folders.AsNoTracking()
            .Where(folder => folder.LibraryId == library.Id
                && folder.TrashedAt != null
                && folder.TrashRootId == folder.Id)
            .Select(folder => new BrowseEntry { Id = folder.Id, IsFolder = true, Name = folder.Name, ModifiedAt = folder.ModifiedAt, SizeBytes = null, Sequence = folder.Sequence });
        if (normalizedType is not ("all" or "folder")) folders = folders.Where(_ => false);
        var files = dbContext.Files.AsNoTracking()
            .Where(file => file.LibraryId == library.Id
                && file.TrashedAt != null
                && file.TrashRootId == file.Id);
        files = ApplyFileTypeFilter(files, normalizedType);
        var entries = files
            .Select(file => new BrowseEntry { Id = file.Id, IsFolder = false, Name = file.Name, ModifiedAt = file.ModifiedAt, SizeBytes = file.SizeBytes, Sequence = file.Sequence });
        var page = await ApplyItemSort(folders.Concat(entries), normalizedSort)
            .Skip(offset).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);

        var folderIds = page.Where(entry => entry.IsFolder).Select(entry => entry.Id).ToArray();
        var fileIds = page.Where(entry => !entry.IsFolder).Select(entry => entry.Id).ToArray();
        var folderFavorites = await dbContext.FolderUserStates.AsNoTracking().Where(state => state.UserId == userId && state.Favorite && folderIds.Contains(state.FolderId)).Select(state => state.FolderId).ToHashSetAsync(cancellationToken);
        var favorites = await dbContext.FileUserStates.AsNoTracking().Where(state => state.UserId == userId && state.Favorite && fileIds.Contains(state.FileId)).Select(state => state.FileId).ToHashSetAsync(cancellationToken);
        var folderById = await dbContext.Folders.AsNoTracking().Where(folder => folderIds.Contains(folder.Id)).ToDictionaryAsync(folder => folder.Id, cancellationToken);
        var fileById = await dbContext.Files.AsNoTracking().Where(file => fileIds.Contains(file.Id)).ToDictionaryAsync(file => file.Id, cancellationToken);
        var tagMap = await TagsForItemsAsync(fileIds, folderIds, cancellationToken);
        return new BrowseItemsRes
        {
            Items = page.Select(entry => entry.IsFolder
                ? ToFolderResponse(folderById[entry.Id], folderFavorites.Contains(entry.Id), tags: tagMap.GetValueOrDefault(entry.Id))
                : ToFileResponse(fileById[entry.Id], favorites.Contains(entry.Id), tags: tagMap.GetValueOrDefault(entry.Id))).ToList(),
            NextCursor = hasMore ? EncodeOffsetCursor(normalizedSort, offset + page.Count) : null
        };
    }

    public async Task<Result<BrowseItemsRes>> BrowseFavoriteFoldersAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        const string sort = "modified_desc";
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var sharedItems = await authorization.SharedItemIdsAsync(userId, cancellationToken);
        var offset = DecodeOffsetCursor(cursor, sort);
        var page = await dbContext.Folders.AsNoTracking()
            .Where(folder => (folder.LibraryId == library.Id || sharedItems.FolderIds.Contains(folder.Id))
                && folder.TrashedAt == null
                && dbContext.FolderUserStates.Any(state => state.UserId == userId && state.FolderId == folder.Id && state.Favorite))
            .OrderByDescending(folder => folder.ModifiedAt)
            .ThenByDescending(folder => folder.Sequence)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        return new BrowseItemsRes
        {
            Items = page.Select(folder => ToFolderResponse(
                folder,
                favorite: true,
                readOnly: sharedItems.FolderIds.Contains(folder.Id))).ToList(),
            NextCursor = hasMore ? EncodeOffsetCursor(sort, offset + page.Count) : null
        };
    }

    public async Task<Result<BrowseItemsRes>> BrowsePhotosAsync(Guid userId, string scope, Guid? folderId, string? cursor, int limit, string type, bool favorite, string sort, int? year, string? captureDate, string? seekDate, Guid? tagId, CancellationToken cancellationToken)
    {
        var normalizedSort = NormalizePhotoSort(sort);
        if (normalizedSort is null) return MediaErrors.Validation("Photo sort must be captured_desc, captured_asc, or added_desc.");
        if (!TryParseTimelineSeekDate(seekDate, out var seekDay))
            return MediaErrors.Validation("Photo timeline seek date must be YYYY-MM-DD.");
        if (seekDay.HasValue && normalizedSort == "added_desc")
            return MediaErrors.Validation("Photo timeline date seeks require captured_desc or captured_asc sorting.");
        if (seekDay.HasValue && (year.HasValue || !string.IsNullOrWhiteSpace(captureDate)))
            return MediaErrors.Validation("Choose either a photo date filter or a timeline seek date.");
        var photoQuery = await BuildPhotoQueryAsync(userId, scope, folderId, type, favorite, year, captureDate, tagId, cancellationToken);
        if (photoQuery.IsFailure) return photoQuery.ErrorValue;
        var (query, libraryId, normalizedScope, normalizedType) = photoQuery.SuccessValue;
        var cursorPosition = DecodePhotoCursor(cursor, normalizedSort);
        if (cursorPosition is null && string.IsNullOrWhiteSpace(cursor) && seekDay.HasValue)
        {
            cursorPosition = new PhotoCursor
            {
                Version = 1,
                Kind = "anchor",
                Sort = normalizedSort,
                Year = seekDay.Value.Year,
                Month = seekDay.Value.Month,
                Day = seekDay.Value.Day
            };
        }
        var navigatingPrevious = cursorPosition is { Kind: "item", Direction: "previous" };
        var baseQuery = query;
        query = ApplyPhotoCursor(query, cursorPosition, normalizedSort);
        var files = await (navigatingPrevious
                ? ApplyReversePhotoSort(query, normalizedSort)
                : ApplyPhotoSort(query, normalizedSort))
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = files.Count > limit;
        if (hasMore) files.RemoveAt(files.Count - 1);
        if (navigatingPrevious) files.Reverse();

        var hasPrevious = files.Count > 0
            && await ApplyPhotoCursor(
                    baseQuery,
                    PhotoItemCursor(normalizedSort, files[0], "previous"),
                    normalizedSort)
                .AnyAsync(cancellationToken);
        var hasNext = files.Count > 0
            && await ApplyPhotoCursor(
                    baseQuery,
                    PhotoItemCursor(normalizedSort, files[^1], "next"),
                    normalizedSort)
                .AnyAsync(cancellationToken);

        var fileIds = files.Select(file => file.Id).ToArray();
        var favoriteIds = await dbContext.FileUserStates.AsNoTracking()
            .Where(state => state.UserId == userId && state.Favorite && fileIds.Contains(state.FileId))
            .Select(state => state.FileId)
            .ToHashSetAsync(cancellationToken);
        var paths = await CatalogPathsAsync(libraryId, includeTrashed: false, cancellationToken);
        var tagMap = await TagsForItemsAsync(fileIds, [], cancellationToken);

        var response = new BrowseItemsRes
        {
            Items = files.Select(file => ToFileResponse(
                file,
                favoriteIds.Contains(file.Id),
                catalogPath: file.ParentFolderId.HasValue ? paths.GetValueOrDefault(file.ParentFolderId.Value) : null,
                tags: tagMap.GetValueOrDefault(file.Id))).ToList(),
            NextCursor = hasNext
                ? EncodePhotoItemCursor(normalizedSort, files[^1], "next")
                : null,
            PreviousCursor = hasPrevious
                ? EncodePhotoItemCursor(normalizedSort, files[0], "previous")
                : null
        };
        logger.LogInformation(
            "[media.photos] timeline query completed scope={Scope} type={MediaType} favorite={Favorite} sort={Sort} year={Year} captureDate={CaptureDate} items={ItemCount} hasMore={HasMore}",
            normalizedScope,
            normalizedType,
            favorite,
            normalizedSort,
            year,
            captureDate,
            response.Items.Count,
            response.NextCursor is not null);
        return response;
    }

    public async Task<Result<PhotoTimelineAnchorsRes>> BrowsePhotoTimelineAnchorsAsync(Guid userId, string scope, Guid? folderId, string type, bool favorite, string sort, int? year, string? captureDate, Guid? tagId, CancellationToken cancellationToken)
    {
        var normalizedSort = NormalizePhotoSort(sort);
        if (normalizedSort is not ("captured_desc" or "captured_asc"))
            return MediaErrors.Validation("Timeline anchors require captured_desc or captured_asc sorting.");

        var photoQuery = await BuildPhotoQueryAsync(userId, scope, folderId, type, favorite, year, captureDate, tagId, cancellationToken);
        if (photoQuery.IsFailure) return photoQuery.ErrorValue;

        var grouped = photoQuery.SuccessValue.Query
            .GroupBy(file => new { file.CapturedAt.Year, file.CapturedAt.Month })
            .Select(group => new { group.Key.Year, group.Key.Month, Count = group.Count() });
        var rows = normalizedSort == "captured_asc"
            ? await grouped.OrderBy(row => row.Year).ThenBy(row => row.Month).ToListAsync(cancellationToken)
            : await grouped.OrderByDescending(row => row.Year).ThenByDescending(row => row.Month).ToListAsync(cancellationToken);

        return new PhotoTimelineAnchorsRes
        {
            TotalCount = rows.Sum(row => row.Count),
            Anchors = rows.Select(row => new PhotoTimelineAnchorRes
            {
                Year = row.Year,
                Month = row.Month,
                Count = row.Count,
                Cursor = EncodePhotoAnchorCursor(normalizedSort, row.Year, row.Month)
            }).ToList()
        };
    }

    public async Task<Result<PhotoTimelineYearsRes>> BrowsePhotoYearsAsync(Guid userId, string scope, Guid? folderId, string type, bool favorite, Guid? tagId, CancellationToken cancellationToken)
    {
        var photoQuery = await BuildPhotoQueryAsync(
            userId,
            scope,
            folderId,
            type,
            favorite,
            year: null,
            captureDate: null,
            tagId,
            cancellationToken);
        if (photoQuery.IsFailure) return photoQuery.ErrorValue;
        var years = await photoQuery.SuccessValue.Query
            .Select(file => file.CapturedAt.Year)
            .Distinct()
            .OrderByDescending(year => year)
            .ToListAsync(cancellationToken);
        return new PhotoTimelineYearsRes { Years = years };
    }

    public async Task<Result<TrashSummaryRes>> GetTrashSummaryAsync(Guid userId, CancellationToken cancellationToken)
    {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var fileCount = await dbContext.Files.AsNoTracking()
            .CountAsync(file => file.LibraryId == library.Id && file.TrashedAt != null, cancellationToken);
        var folderCount = await dbContext.Folders.AsNoTracking()
            .CountAsync(folder => folder.LibraryId == library.Id && folder.TrashedAt != null, cancellationToken);
        var originals = await TrashDeletableBlobs(library.Id)
            .Select(blob => new { blob.Sha256, blob.SizeBytes })
            .ToListAsync(cancellationToken);
        return new TrashSummaryRes
        {
            FileCount = fileCount,
            FolderCount = folderCount,
            OriginalCount = originals.Count,
            OriginalBytes = originals.Sum(blob => blob.SizeBytes)
        };
    }

    public async Task<Result<TrashPreviewRes>> PreviewTrashAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        var selectedIds = itemIds.Distinct().ToArray();
        if (selectedIds.Length == 0) return MediaErrors.Validation("Choose at least one item to move to Trash.");
        if (selectedIds.Length > 300) return MediaErrors.Validation("Choose 300 or fewer item roots to move to Trash.");

        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var selectedFolders = await dbContext.Folders.AsNoTracking()
            .Where(folder => selectedIds.Contains(folder.Id) && folder.LibraryId == library.Id && folder.TrashedAt == null)
            .Select(folder => new { folder.Id, folder.SystemRole })
            .ToListAsync(cancellationToken);
        if (selectedFolders.Any(folder => folder.SystemRole is not null))
            return MediaErrors.Conflict("Protected Media and Documents folders cannot be moved to Trash.");
        var selectedFileIds = await dbContext.Files.AsNoTracking()
            .Where(file => selectedIds.Contains(file.Id) && file.LibraryId == library.Id && file.TrashedAt == null)
            .Select(file => file.Id)
            .ToListAsync(cancellationToken);
        if (selectedFolders.Count + selectedFileIds.Count != selectedIds.Length)
            return MediaErrors.NotFound("One or more selected items were not found in your active library.");

        var affectedFolderIds = new HashSet<Guid>();
        foreach (var selectedFolder in selectedFolders)
            affectedFolderIds.UnionWith(await DescendantFolderIdsAsync(library.Id, selectedFolder.Id, cancellationToken));
        var activeFolderCount = affectedFolderIds.Count == 0
            ? 0
            : await dbContext.Folders.AsNoTracking().CountAsync(
                folder => folder.LibraryId == library.Id && folder.TrashedAt == null && affectedFolderIds.Contains(folder.Id),
                cancellationToken);

        var affectedFileIds = selectedFileIds.ToHashSet();
        if (affectedFolderIds.Count > 0)
        {
            var descendantFileIds = await dbContext.Files.AsNoTracking()
                .Where(file => file.LibraryId == library.Id && file.TrashedAt == null
                    && file.ParentFolderId.HasValue && affectedFolderIds.Contains(file.ParentFolderId.Value))
                .Select(file => file.Id)
                .ToListAsync(cancellationToken);
            affectedFileIds.UnionWith(descendantFileIds);
        }

        var photoItemCount = affectedFileIds.Count == 0
            ? 0
            : await dbContext.Files.AsNoTracking().CountAsync(
                file => affectedFileIds.Contains(file.Id)
                    && (file.MimeType.StartsWith("image/") || file.MimeType.StartsWith("video/")),
                cancellationToken);
        var albumCount = affectedFileIds.Count == 0
            ? 0
            : await dbContext.AlbumItems.AsNoTracking()
                .Where(item => affectedFileIds.Contains(item.FileId))
                .Select(item => item.AlbumId)
                .Distinct()
                .CountAsync(cancellationToken);

        return new TrashPreviewRes
        {
            SelectedRootCount = selectedIds.Length,
            FileCount = affectedFileIds.Count,
            FolderCount = activeFolderCount,
            PhotoItemCount = photoItemCount,
            AlbumCount = albumCount
        };
    }

    public async Task<Result<StorageSummaryRes>> GetStorageSummaryAsync(Guid userId, bool refresh, CancellationToken cancellationToken)
    {
        var cacheKey = StorageSummaryCacheKey(userId);
        if (!refresh && storageSummaryCache.TryGetValue(cacheKey, out StorageSummaryRes? cached) && cached is not null) return cached;

        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var catalogFileCount = await dbContext.Files.AsNoTracking()
            .LongCountAsync(file => file.LibraryId == library.Id, cancellationToken);
        var referencedBlobHashes =
            from version in dbContext.FileVersions.AsNoTracking()
            join file in dbContext.Files.AsNoTracking() on version.FileId equals file.Id
            where file.LibraryId == library.Id
            select version.BlobSha256;
        var storedBlobs = dbContext.Blobs.AsNoTracking()
            .Where(blob => referencedBlobHashes.Distinct().Contains(blob.Sha256));
        var storedOriginalCount = await storedBlobs.CountAsync(cancellationToken);
        var storedOriginalBytes = await storedBlobs
            .Select(blob => (long?)blob.SizeBytes)
            .SumAsync(cancellationToken) ?? 0;
        var reclaimableBlobs = TrashDeletableBlobs(library.Id);
        var reclaimableTrashOriginalCount = await reclaimableBlobs.CountAsync(cancellationToken);
        var reclaimableTrashBytes = await reclaimableBlobs
            .Select(blob => (long?)blob.SizeBytes)
            .SumAsync(cancellationToken) ?? 0;
        var summary = new StorageSummaryRes
        {
            CatalogFileCount = catalogFileCount,
            UniqueOriginalCount = storedOriginalCount,
            StoredOriginalBytes = storedOriginalBytes,
            ReclaimableTrashOriginalCount = reclaimableTrashOriginalCount,
            ReclaimableTrashBytes = reclaimableTrashBytes,
            CalculatedAt = DateTime.UtcNow
        };
        storageSummaryCache.Set(cacheKey, summary);
        logger.LogInformation("[media.storage] summary calculated uniqueOriginals={UniqueOriginalCount} storedBytes={StoredOriginalBytes} reclaimableTrashOriginals={ReclaimableTrashOriginalCount} reclaimableTrashBytes={ReclaimableTrashBytes}", summary.UniqueOriginalCount, summary.StoredOriginalBytes, summary.ReclaimableTrashOriginalCount, summary.ReclaimableTrashBytes);
        return summary;
    }

    public async Task<Result<BrowseItemsRes>> SearchAsync(Guid userId, string query, string scope, string photoScope, string type, string? cursor, int limit, Guid? tagId, CancellationToken cancellationToken)
    {
        var ftsQuery = ToFtsQuery(query);
        if (ftsQuery is null) return MediaErrors.Validation("Enter at least two letters or numbers to search your catalog.");
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim().ToLowerInvariant();
        var normalizedPhotoScope = string.IsNullOrWhiteSpace(photoScope) ? "folder" : photoScope.Trim().ToLowerInvariant();
        var normalizedType = NormalizeFileType(type);
        if (normalizedScope is not ("photos" or "trash")) return MediaErrors.Validation("Search scope must be photos or trash.");
        if (normalizedPhotoScope is not ("all" or "folder")) return MediaErrors.Validation("Photo search scope must be all or folder.");
        if (normalizedType is null) return MediaErrors.Validation("File type must be all, folder, document, pdf, spreadsheet, image, or video.");

        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        if (!await TagBelongsToLibraryAsync(tagId, library.Id, cancellationToken))
            return MediaErrors.NotFound("The requested tag was not found in your library.");
        var mediaRootId = await libraryBootstrapper.SystemFolderIdAsync(
            library.Id,
            LibraryBootstrapper.MediaFolderRole,
            cancellationToken);
        var mediaFolderIds = await DescendantFolderIdsAsync(library.Id, mediaRootId, cancellationToken);
        var offset = DecodeSearchCursor(cursor);
        var candidates = await SearchCandidatesAsync(library.Id, ftsQuery, normalizedScope, normalizedPhotoScope, normalizedType, tagId, offset, limit, cancellationToken);
        if (candidates.Count == 0) return new BrowseItemsRes();

        var pageCandidates = candidates.Take(limit).ToArray();
        var candidateIds = pageCandidates.Select(candidate => candidate.Id).ToArray();
        var folderQuery = dbContext.Folders.AsNoTracking()
            .Where(folder => candidateIds.Contains(folder.Id) && folder.LibraryId == library.Id
                && normalizedScope != "photos"
                && (normalizedScope == "trash" ? folder.TrashedAt != null : folder.TrashedAt == null));
        if (tagId.HasValue) folderQuery = folderQuery.Where(folder => dbContext.FolderTags.Any(item => item.FolderId == folder.Id && item.TagId == tagId));
        if (normalizedType is not ("all" or "folder")) folderQuery = folderQuery.Where(_ => false);
        var folders = await folderQuery.ToDictionaryAsync(folder => folder.Id, cancellationToken);
        var filesQuery = dbContext.Files.AsNoTracking()
            .Where(file => candidateIds.Contains(file.Id) && file.LibraryId == library.Id
                && (normalizedScope == "trash" ? file.TrashedAt != null : file.TrashedAt == null));
        if (normalizedScope == "photos")
        {
            filesQuery = filesQuery.Where(file =>
                file.MimeType.StartsWith("image/") || file.MimeType.StartsWith("video/"));
            if (normalizedPhotoScope == "folder")
                filesQuery = filesQuery.Where(file =>
                    file.ParentFolderId.HasValue && mediaFolderIds.Contains(file.ParentFolderId.Value));
        }
        filesQuery = ApplyFileTypeFilter(filesQuery, normalizedType);
        if (tagId.HasValue) filesQuery = filesQuery.Where(file => dbContext.FileTags.Any(item => item.FileId == file.Id && item.TagId == tagId));
        var files = await filesQuery.ToDictionaryAsync(file => file.Id, cancellationToken);
        var fileIds = files.Keys.ToArray();
        var folderIds = folders.Keys.ToArray();
        var favoriteFiles = await dbContext.FileUserStates.AsNoTracking().Where(state => state.UserId == userId && state.Favorite && fileIds.Contains(state.FileId)).Select(state => state.FileId).ToHashSetAsync(cancellationToken);
        var favoriteFolders = await dbContext.FolderUserStates.AsNoTracking().Where(state => state.UserId == userId && state.Favorite && folderIds.Contains(state.FolderId)).Select(state => state.FolderId).ToHashSetAsync(cancellationToken);
        var paths = await CatalogPathsAsync(library.Id, normalizedScope == "trash", cancellationToken);
        var tagMap = await TagsForItemsAsync(fileIds, folderIds, cancellationToken);
        var items = pageCandidates.Select(candidate =>
        {
            if (candidate.Kind == "folder" && folders.TryGetValue(candidate.Id, out var folder))
                return ToFolderResponse(folder, favoriteFolders.Contains(folder.Id), paths.GetValueOrDefault(folder.Id), tags: tagMap.GetValueOrDefault(folder.Id));
            return candidate.Kind == "file" && files.TryGetValue(candidate.Id, out var file)
                ? ToFileResponse(file, favoriteFiles.Contains(file.Id), file.ParentFolderId.HasValue ? paths.GetValueOrDefault(file.ParentFolderId.Value) : null, tags: tagMap.GetValueOrDefault(file.Id))
                : null;
        }).Where(item => item is not null).Cast<MediaItemRes>().ToList();
        var hasMore = candidates.Count > limit;
        return new BrowseItemsRes
        {
            Items = items,
            NextCursor = hasMore ? EncodeSearchCursor(offset + pageCandidates.Length) : null
        };
    }

    public async Task<Result<BrowseItemsRes>> QueryFilesAsync(
        Guid userId,
        FileQueryOptions options,
        CancellationToken cancellationToken)
    {
        var query = options.Query;
        var location = options.Location;
        var folderId = options.FolderId;
        var includePhotos = options.IncludePhotos;
        var favorite = options.Favorite;
        var type = options.Type;
        var sort = options.Sort;
        var cursor = options.Cursor;
        var limit = options.Limit;
        var tagId = options.TagId;
        var normalizedLocation = string.IsNullOrWhiteSpace(location) ? "children" : location.Trim().ToLowerInvariant();
        var normalizedType = NormalizeFileType(type);
        var normalizedSort = NormalizeItemSort(sort);
        if (normalizedLocation is not ("children" or "subtree" or "all"))
            return MediaErrors.Validation("Files location must be children, subtree, or all.");
        if (normalizedType is null)
            return MediaErrors.Validation("File type must be all, folder, document, pdf, spreadsheet, image, or video.");
        if (normalizedSort is null)
            return MediaErrors.Validation("Item sort must be name_asc, name_desc, modified_desc, modified_asc, size_desc, or size_asc.");
        if (includePhotos && normalizedLocation != "all")
            return MediaErrors.Validation("Photos can be included only when discovering all Files.");

        var normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : ToFtsQuery(query);
        if (!string.IsNullOrWhiteSpace(query) && normalizedQuery is null)
            return MediaErrors.Validation("Enter at least two letters or numbers to search your catalog.");

        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        if (!await TagBelongsToLibraryAsync(tagId, library.Id, cancellationToken))
            return MediaErrors.NotFound("The requested tag was not found in your library.");

        var documentsRootId = await libraryBootstrapper.SystemFolderIdAsync(
            library.Id,
            LibraryBootstrapper.DocumentsFolderRole,
            cancellationToken);
        var documentsFolderIds = await DescendantFolderIdsAsync(library.Id, documentsRootId, cancellationToken);
        var rootFolderId = normalizedLocation != "all" && folderId.HasValue
            ? folderId.Value
            : documentsRootId;
        if (!documentsFolderIds.Contains(rootFolderId))
            return MediaErrors.NotFound("The requested Files folder was not found.");

        var includedParentIds = normalizedLocation == "children"
            ? new HashSet<Guid> { rootFolderId }
            : await DescendantFolderIdsAsync(library.Id, rootFolderId, cancellationToken);
        if (includePhotos)
        {
            var mediaRootId = await libraryBootstrapper.SystemFolderIdAsync(
                library.Id,
                LibraryBootstrapper.MediaFolderRole,
                cancellationToken);
            includedParentIds.UnionWith(await DescendantFolderIdsAsync(library.Id, mediaRootId, cancellationToken));
        }

        List<SearchCandidate> pageCandidates;
        bool hasMore;
        int offset;
        if (normalizedQuery is not null)
        {
            offset = DecodeSearchCursor(cursor);
            var candidates = await QueryFileCandidatesAsync(
                library.Id,
                userId,
                normalizedQuery,
                rootFolderId,
                normalizedLocation != "children",
                includePhotos,
                favorite,
                normalizedType,
                tagId,
                normalizedSort,
                offset,
                limit,
                cancellationToken);
            hasMore = candidates.Count > limit;
            pageCandidates = candidates.Take(limit).ToList();
        }
        else
        {
            offset = DecodeOffsetCursor(cursor, normalizedSort);
            var folders = dbContext.Folders.AsNoTracking()
                .Where(folder => folder.LibraryId == library.Id
                    && folder.TrashedAt == null
                    && folder.ParentFolderId.HasValue
                    && includedParentIds.Contains(folder.ParentFolderId.Value)
                    && (!favorite || dbContext.FolderUserStates.Any(state => state.UserId == userId && state.FolderId == folder.Id && state.Favorite)))
                .Select(folder => new BrowseEntry { Id = folder.Id, IsFolder = true, Name = folder.Name, ModifiedAt = folder.ModifiedAt, SizeBytes = null, Sequence = folder.Sequence });
            if (tagId.HasValue)
                folders = folders.Where(entry => dbContext.FolderTags.Any(assignment => assignment.FolderId == entry.Id && assignment.TagId == tagId));
            if (normalizedType is not ("all" or "folder")) folders = folders.Where(_ => false);

            var files = dbContext.Files.AsNoTracking()
                .Where(file => file.LibraryId == library.Id
                    && file.TrashedAt == null
                    && file.ParentFolderId.HasValue
                    && includedParentIds.Contains(file.ParentFolderId.Value)
                    && (!favorite || dbContext.FileUserStates.Any(state => state.UserId == userId && state.FileId == file.Id && state.Favorite)));
            files = ApplyFileTypeFilter(files, normalizedType);
            if (tagId.HasValue)
                files = files.Where(file => dbContext.FileTags.Any(assignment => assignment.FileId == file.Id && assignment.TagId == tagId));
            var entries = files.Select(file => new BrowseEntry { Id = file.Id, IsFolder = false, Name = file.Name, ModifiedAt = file.ModifiedAt, SizeBytes = file.SizeBytes, Sequence = file.Sequence });
            var page = await ApplyItemSort(folders.Concat(entries), normalizedSort)
                .Skip(offset)
                .Take(limit + 1)
                .ToListAsync(cancellationToken);
            hasMore = page.Count > limit;
            if (hasMore) page.RemoveAt(page.Count - 1);
            pageCandidates = page.Select(entry => new SearchCandidate(entry.Id, entry.IsFolder ? "folder" : "file")).ToList();
        }

        if (pageCandidates.Count == 0) return new BrowseItemsRes();
        var candidateIds = pageCandidates.Select(candidate => candidate.Id).ToArray();
        var folderById = await dbContext.Folders.AsNoTracking()
            .Where(folder => candidateIds.Contains(folder.Id) && folder.LibraryId == library.Id && folder.TrashedAt == null)
            .ToDictionaryAsync(folder => folder.Id, cancellationToken);
        var fileById = await dbContext.Files.AsNoTracking()
            .Where(file => candidateIds.Contains(file.Id) && file.LibraryId == library.Id && file.TrashedAt == null)
            .ToDictionaryAsync(file => file.Id, cancellationToken);
        var fileIds = fileById.Keys.ToArray();
        var folderIds = folderById.Keys.ToArray();
        var favoriteFiles = await dbContext.FileUserStates.AsNoTracking()
            .Where(state => state.UserId == userId && state.Favorite && fileIds.Contains(state.FileId))
            .Select(state => state.FileId)
            .ToHashSetAsync(cancellationToken);
        var favoriteFolders = await dbContext.FolderUserStates.AsNoTracking()
            .Where(state => state.UserId == userId && state.Favorite && folderIds.Contains(state.FolderId))
            .Select(state => state.FolderId)
            .ToHashSetAsync(cancellationToken);
        var paths = normalizedLocation == "children" && normalizedQuery is null
            ? new Dictionary<Guid, string>()
            : await CatalogPathsAsync(library.Id, includeTrashed: false, cancellationToken);
        var tagMap = await TagsForItemsAsync(fileIds, folderIds, cancellationToken);
        var items = pageCandidates.Select(candidate =>
        {
            if (candidate.Kind == "folder" && folderById.TryGetValue(candidate.Id, out var folder))
                return ToFolderResponse(folder, favoriteFolders.Contains(folder.Id), paths.GetValueOrDefault(folder.Id), tags: tagMap.GetValueOrDefault(folder.Id));
            return candidate.Kind == "file" && fileById.TryGetValue(candidate.Id, out var file)
                ? ToFileResponse(file, favoriteFiles.Contains(file.Id), file.ParentFolderId.HasValue ? paths.GetValueOrDefault(file.ParentFolderId.Value) : null, tags: tagMap.GetValueOrDefault(file.Id))
                : null;
        }).Where(item => item is not null).Cast<MediaItemRes>().ToList();

        return new BrowseItemsRes
        {
            Items = items,
            NextCursor = hasMore
                ? normalizedQuery is null
                    ? EncodeOffsetCursor(normalizedSort, offset + pageCandidates.Count)
                    : EncodeSearchCursor(offset + pageCandidates.Count)
                : null
        };
    }

    public async Task<Result<MediaItemRes>> CreateFolderAsync(Guid userId, CreateFolderReq request, CancellationToken cancellationToken)
    {
        var nameResult = CatalogItemPolicy.NormalizeName(request.Name, "Folder");
        if (nameResult.IsFailure) return nameResult.ErrorValue;
        var name = nameResult.SuccessValue;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var parentId = request.ParentId ?? await libraryBootstrapper.SystemFolderIdAsync(
            library.Id,
            LibraryBootstrapper.DocumentsFolderRole,
            cancellationToken);

        {
            var parentExists = await dbContext.Folders.AnyAsync(
                folder => folder.Id == parentId && folder.LibraryId == library.Id,
                cancellationToken);
            if (!parentExists) return MediaErrors.NotFound("The destination folder was not found.");
        }

        var now = DateTime.UtcNow;
        var folder = new CatalogFolder
        {
            LibraryId = library.Id,
            ParentFolderId = parentId,
            Name = name,
            Sequence = ++library.NextItemSequence,
            CreatedAt = now,
            ModifiedAt = now
        };
        dbContext.Folders.Add(folder);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "[media.catalog] folder created itemId={ItemId} parentId={ParentId}",
            folder.Id,
            folder.ParentFolderId);

        return new MediaItemRes
        {
            Id = folder.Id,
            Name = folder.Name,
            Kind = "folder",
            ParentId = folder.ParentFolderId,
            CreatedAt = folder.CreatedAt,
            ModifiedAt = folder.ModifiedAt,
            LocalState = "remote",
            SyncState = "synced"
        };
    }

    public async Task<Result<MediaItemRes>> GetItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var folder = await dbContext.Folders.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (folder is not null) return await ToFolderResponseAsync(userId, folder, cancellationToken);

        var file = await dbContext.Files.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (file is not null) return await ToFileResponseAsync(userId, file, cancellationToken, includeImportedDetails: true);

        folder = await dbContext.Folders.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (folder is not null && await authorization.FolderAccessAsync(userId, itemId, requireActive: true, cancellationToken) is { IsOwner: false })
            return await ToFolderResponseAsync(userId, folder, cancellationToken, readOnly: true);
        file = await dbContext.Files.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (file is not null && await authorization.FileAccessAsync(userId, itemId, requireActive: true, cancellationToken) is { IsOwner: false })
            return await ToFileResponseAsync(userId, file, cancellationToken, includeImportedDetails: true, readOnly: true);
        return MediaErrors.NotFound("The requested item was not found in your library.");
    }

    public async Task<Result<MediaItemRes>> RenameItemAsync(Guid userId, Guid itemId, RenameItemReq request, CancellationToken cancellationToken)
    {
        var nameResult = CatalogItemPolicy.NormalizeName(request.Name, "Item");
        if (nameResult.IsFailure) return nameResult.ErrorValue;
        var name = nameResult.SuccessValue;
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var folder = await dbContext.Folders.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (folder is not null)
        {
            var mutable = CatalogItemPolicy.EnsureMutable(folder, "renamed");
            if (mutable.IsFailure) return mutable.ErrorValue;
            var active = CatalogItemPolicy.EnsureActive(folder.TrashedAt);
            if (active.IsFailure) return active.ErrorValue;
            folder.Name = name;
            folder.ModifiedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[media.catalog] folder renamed itemId={ItemId}", folder.Id);
            return await ToFolderResponseAsync(userId, folder, cancellationToken);
        }

        var file = await dbContext.Files.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (file is null) return MediaErrors.NotFound("The requested item was not found in your library.");
        var fileActive = CatalogItemPolicy.EnsureActive(file.TrashedAt);
        if (fileActive.IsFailure) return fileActive.ErrorValue;
        file.Name = name;
        file.ModifiedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.catalog] file renamed itemId={ItemId}", file.Id);
        return await ToFileResponseAsync(userId, file, cancellationToken);
    }

    public async Task<Result<MediaItemRes>> MoveItemAsync(Guid userId, Guid itemId, MoveItemReq request, CancellationToken cancellationToken)
    {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var destinationParentId = request.ParentId ?? await libraryBootstrapper.SystemFolderIdAsync(
            library.Id,
            LibraryBootstrapper.DocumentsFolderRole,
            cancellationToken);
        var destination = await libraryBootstrapper.EnsureParentAsync(library.Id, destinationParentId, requireActive: true, cancellationToken);
        if (destination.IsFailure) return destination.ErrorValue;
        var folder = await dbContext.Folders.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (folder is not null)
        {
            var mutable = CatalogItemPolicy.EnsureMutable(folder, "moved");
            if (mutable.IsFailure) return mutable.ErrorValue;
            var active = CatalogItemPolicy.EnsureActive(folder.TrashedAt);
            if (active.IsFailure) return active.ErrorValue;
            if (destinationParentId == folder.Id || await WouldCreateFolderCycleAsync(library.Id, folder.Id, destinationParentId, cancellationToken))
            {
                return MediaErrors.Validation("A folder cannot be moved into itself or one of its descendants.");
            }
            folder.ParentFolderId = destinationParentId;
            folder.ModifiedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[media.catalog] folder moved itemId={ItemId} parentId={ParentId}", folder.Id, folder.ParentFolderId);
            return await ToFolderResponseAsync(userId, folder, cancellationToken);
        }

        var file = await dbContext.Files.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (file is null) return MediaErrors.NotFound("The requested item was not found in your library.");
        var fileActive = CatalogItemPolicy.EnsureActive(file.TrashedAt);
        if (fileActive.IsFailure) return fileActive.ErrorValue;
        file.ParentFolderId = destinationParentId;
        file.ModifiedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.catalog] file moved itemId={ItemId} parentId={ParentId}", file.Id, file.ParentFolderId);
        return await ToFileResponseAsync(userId, file, cancellationToken);
    }

    public async Task<Result<MediaItemRes>> CopyFileAsync(Guid userId, Guid itemId, CopyFileReq request, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var source = await dbContext.Files.AsNoTracking()
            .SingleOrDefaultAsync(file => file.Id == itemId && file.LibraryId == library.Id, cancellationToken);
        if (source is null) return MediaErrors.NotFound("The requested file was not found in your library.");
        var active = CatalogItemPolicy.EnsureActive(source.TrashedAt);
        if (active.IsFailure) return active.ErrorValue;

        var destinationParentId = request.ParentId ?? source.ParentFolderId
            ?? await libraryBootstrapper.SystemFolderIdAsync(library.Id, LibraryBootstrapper.DocumentsFolderRole, cancellationToken);
        var destination = await libraryBootstrapper.EnsureParentAsync(library.Id, destinationParentId, requireActive: true, cancellationToken);
        if (destination.IsFailure) return destination.ErrorValue;

        var siblingNames = await dbContext.Files.AsNoTracking()
            .Where(file => file.LibraryId == library.Id && file.ParentFolderId == destinationParentId && file.TrashedAt == null)
            .Select(file => file.Name)
            .ToListAsync(cancellationToken);
        var copyName = CopyName(source.Name, siblingNames);
        var sourceVersion = await dbContext.FileVersions.AsNoTracking()
            .SingleAsync(version => version.Id == source.CurrentVersionId, cancellationToken);
        var sourceMetadata = await dbContext.FileVersionMetadata.AsNoTracking()
            .SingleOrDefaultAsync(metadata => metadata.FileVersionId == source.CurrentVersionId, cancellationToken);
        var now = DateTime.UtcNow;
        var copy = new CatalogFile
        {
            LibraryId = library.Id,
            ParentFolderId = destinationParentId,
            Name = copyName,
            MimeType = source.MimeType,
            SizeBytes = source.SizeBytes,
            Sequence = ++library.NextItemSequence,
            CreatedAt = now,
            ModifiedAt = now,
            CapturedAt = source.CapturedAt,
            CaptureDateSource = source.CaptureDateSource,
            UserCapturedAt = source.UserCapturedAt,
            ImportedCapturedAt = source.ImportedCapturedAt,
            EmbeddedCapturedAt = source.EmbeddedCapturedAt,
            FilenameCapturedAt = source.FilenameCapturedAt,
            Description = source.Description,
            Latitude = source.Latitude,
            Longitude = source.Longitude,
            AltitudeMeters = source.AltitudeMeters,
            Width = source.Width,
            Height = source.Height,
            Orientation = source.Orientation,
            MediaFormat = source.MediaFormat,
            MetadataState = source.MetadataState == "processing" ? "pending" : source.MetadataState,
            MetadataAttemptCount = source.MetadataAttemptCount,
            MetadataErrorCode = source.MetadataErrorCode,
            MetadataErrorMessage = source.MetadataErrorMessage,
            MetadataProcessedAt = source.MetadataProcessedAt,
            RecoveryManifestState = source.RecoveryManifestState
        };
        var copyVersion = new FileVersion
        {
            FileId = copy.Id,
            BlobSha256 = sourceVersion.BlobSha256,
            SizeBytes = sourceVersion.SizeBytes,
            MimeType = sourceVersion.MimeType,
            DeclaredMimeType = sourceVersion.DeclaredMimeType,
            OriginalName = sourceVersion.OriginalName,
            UploaderUserId = sourceVersion.UploaderUserId,
            CreatedAt = now
        };
        copy.CurrentVersionId = copyVersion.Id;
        dbContext.Files.Add(copy);
        dbContext.FileVersions.Add(copyVersion);
        dbContext.FileVersionMetadata.Add(CopyVersionMetadata(sourceMetadata, copyVersion.Id, source));

        var tagIds = await dbContext.FileTags.AsNoTracking()
            .Where(tag => tag.FileId == source.Id)
            .Select(tag => tag.TagId)
            .ToListAsync(cancellationToken);
        dbContext.FileTags.AddRange(tagIds.Select(tagId => new FileTag
        {
            FileId = copy.Id,
            TagId = tagId,
            AddedByUserId = userId,
            AddedAt = now
        }));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("[media.catalog] file copied sourceItemId={SourceItemId} itemId={ItemId} parentId={ParentId}", source.Id, copy.Id, copy.ParentFolderId);
        return await ToFileResponseAsync(userId, copy, cancellationToken);
    }

    public async Task<Result<MediaItemRes>> SetFavoriteAsync(Guid userId, Guid itemId, SetFavoriteReq request, CancellationToken cancellationToken)
    {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var folder = await dbContext.Folders.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        var readOnly = false;
        if (folder is null)
        {
            folder = await dbContext.Folders.SingleOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
            if (folder is not null && await authorization.FolderAccessAsync(userId, itemId, requireActive: true, cancellationToken) is { IsOwner: false }) readOnly = true;
            else folder = null;
        }
        if (folder is not null)
        {
            var active = CatalogItemPolicy.EnsureActive(folder.TrashedAt);
            if (active.IsFailure) return active.ErrorValue;
            var folderState = await dbContext.FolderUserStates.SingleOrDefaultAsync(candidate => candidate.UserId == userId && candidate.FolderId == itemId, cancellationToken);
            if (folderState is null && request.Favorite)
            {
                dbContext.FolderUserStates.Add(new FolderUserState { UserId = userId, FolderId = itemId, Favorite = true });
            }
            else if (folderState is not null)
            {
                folderState.Favorite = request.Favorite;
                folderState.ModifiedAt = DateTime.UtcNow;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[media.favorite] folder updated itemId={ItemId} favorite={Favorite}", itemId, request.Favorite);
            return ToFolderResponse(folder, request.Favorite, readOnly: readOnly);
        }

        var file = await dbContext.Files.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (file is null)
        {
            file = await dbContext.Files.SingleOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
            if (file is not null && await authorization.FileAccessAsync(userId, itemId, requireActive: true, cancellationToken) is { IsOwner: false }) readOnly = true;
            else file = null;
        }
        if (file is null) return MediaErrors.NotFound("The requested item was not found in your library.");
        var fileActive = CatalogItemPolicy.EnsureActive(file.TrashedAt);
        if (fileActive.IsFailure) return fileActive.ErrorValue;

        var state = await dbContext.FileUserStates.SingleOrDefaultAsync(candidate => candidate.UserId == userId && candidate.FileId == itemId, cancellationToken);
        if (state is null && request.Favorite)
        {
            dbContext.FileUserStates.Add(new FileUserState { UserId = userId, FileId = itemId, Favorite = true });
        }
        else if (state is not null)
        {
            state.Favorite = request.Favorite;
            state.ModifiedAt = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.favorite] file updated itemId={ItemId} favorite={Favorite}", itemId, request.Favorite);
        return ToFileResponse(file, request.Favorite, readOnly: readOnly);
    }

    public async Task<Result<MediaItemRes>> TrashItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var now = DateTime.UtcNow;
        var folder = await dbContext.Folders.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (folder is not null)
        {
            var mutable = CatalogItemPolicy.EnsureMutable(folder, "trashed");
            if (mutable.IsFailure) return mutable.ErrorValue;
            var active = CatalogItemPolicy.EnsureActive(folder.TrashedAt);
            if (active.IsFailure) return active.ErrorValue;
            var folderIds = await DescendantFolderIdsAsync(library.Id, folder.Id, cancellationToken);
            var folders = await dbContext.Folders.Where(candidate => folderIds.Contains(candidate.Id) && candidate.TrashedAt == null).ToListAsync(cancellationToken);
            var files = await dbContext.Files.Where(candidate => candidate.LibraryId == library.Id && candidate.ParentFolderId.HasValue && folderIds.Contains(candidate.ParentFolderId.Value) && candidate.TrashedAt == null).ToListAsync(cancellationToken);
            foreach (var descendant in folders) CatalogItemPolicy.MarkTrashed(descendant, folder.Id, now);
            foreach (var descendant in files) CatalogItemPolicy.MarkTrashed(descendant, folder.Id, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            InvalidateStorageSummary(userId);
            logger.LogInformation(
                "[media.trash] folder subtree trashed itemId={ItemId} folders={FolderCount} files={FileCount}",
                folder.Id,
                folders.Count,
                files.Count);
            return await ToFolderResponseAsync(userId, folder, cancellationToken);
        }

        var file = await dbContext.Files.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (file is null) return MediaErrors.NotFound("The requested item was not found in your library.");
        var fileActive = CatalogItemPolicy.EnsureActive(file.TrashedAt);
        if (fileActive.IsFailure) return fileActive.ErrorValue;
        CatalogItemPolicy.MarkTrashed(file, file.Id, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        InvalidateStorageSummary(userId);
        logger.LogInformation("[media.trash] file trashed itemId={ItemId}", file.Id);
        return await ToFileResponseAsync(userId, file, cancellationToken);
    }

    public async Task<Result<MediaItemRes>> RestoreItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var now = DateTime.UtcNow;
        var folder = await dbContext.Folders.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (folder is not null)
        {
            var rootId = folder.TrashRootId ?? folder.Id;
            var folders = await dbContext.Folders.Where(candidate => candidate.LibraryId == library.Id && candidate.TrashRootId == rootId).ToListAsync(cancellationToken);
            var files = await dbContext.Files.Where(candidate => candidate.LibraryId == library.Id && candidate.TrashRootId == rootId).ToListAsync(cancellationToken);
            foreach (var descendant in folders) CatalogItemPolicy.Restore(descendant, now);
            foreach (var descendant in files) CatalogItemPolicy.Restore(descendant, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            InvalidateStorageSummary(userId);
            logger.LogInformation(
                "[media.trash] folder subtree restored itemId={ItemId} folders={FolderCount} files={FileCount}",
                folder.Id,
                folders.Count,
                files.Count);
            return await ToFolderResponseAsync(userId, folder, cancellationToken);
        }

        var file = await dbContext.Files.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.LibraryId == library.Id, cancellationToken);
        if (file is null) return MediaErrors.NotFound("The requested item was not found in your library.");
        CatalogItemPolicy.Restore(file, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        InvalidateStorageSummary(userId);
        logger.LogInformation("[media.trash] file restored itemId={ItemId}", file.Id);
        return await ToFileResponseAsync(userId, file, cancellationToken);
    }

    public async Task<Result<ClearTrashRes>> ClearTrashAsync(Guid userId, ClearTrashReq request, CancellationToken cancellationToken)
    {
        if (!request.Confirmed) return MediaErrors.Validation("Confirm clearing trash before permanently deleting its files.");
        if (!string.Equals(request.ConfirmationPhrase, "DELETE", StringComparison.Ordinal)) return MediaErrors.Validation("Type DELETE to confirm permanently deleting your trash and unshared stored originals.");
        if (request.ExpectedFileCount < 0 || request.ExpectedFolderCount < 0 || request.ExpectedOriginalCount < 0 || request.ExpectedOriginalBytes < 0)
        {
            return MediaErrors.Validation("Trash deletion counts and bytes cannot be negative.");
        }

        await OriginalMutationGate.WaitAsync(cancellationToken);
        try
        {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var trashedFolders = await dbContext.Folders
            .Where(folder => folder.LibraryId == library.Id && folder.TrashedAt != null)
            .ToListAsync(cancellationToken);
        var trashedFiles = await dbContext.Files
            .Where(file => file.LibraryId == library.Id && file.TrashedAt != null)
            .ToListAsync(cancellationToken);
        var deletableBlobs = await TrashDeletableBlobs(library.Id).ToListAsync(cancellationToken);
        var deletableBytes = deletableBlobs.Sum(blob => blob.SizeBytes);
        if (trashedFiles.Count != request.ExpectedFileCount
            || trashedFolders.Count != request.ExpectedFolderCount
            || deletableBlobs.Count != request.ExpectedOriginalCount
            || deletableBytes != request.ExpectedOriginalBytes)
        {
            return MediaErrors.Conflict("Trash or stored-original counts changed before deletion. Review the current totals and confirm again.");
        }
        var deletionRequestedAt = DateTime.UtcNow;
        foreach (var blob in deletableBlobs)
        {
            blob.LifecycleState = Blob.PendingPermanentDeletionLifecycleState;
            blob.DeletionRequestedAt ??= deletionRequestedAt;
            blob.DeletionError = null;
        }
        var folderIds = trashedFolders.Select(folder => folder.Id).ToArray();
        var fileIds = trashedFiles.Select(file => file.Id).ToArray();
        var uploads = await dbContext.UploadSessions
            .Where(upload => upload.LibraryId == library.Id
                && ((upload.ParentFolderId.HasValue && folderIds.Contains(upload.ParentFolderId.Value))
                    || (upload.CatalogFileId.HasValue && fileIds.Contains(upload.CatalogFileId.Value))))
            .ToListAsync(cancellationToken);
        var cancelledUploadStagingFiles = uploads
            .Where(upload => upload.State != "completed")
            .Select(StagingPath)
            .ToArray();

        foreach (var upload in uploads.Where(upload => upload.State == "completed"))
        {
            if (upload.ParentFolderId.HasValue && folderIds.Contains(upload.ParentFolderId.Value)) upload.ParentFolderId = null;
            if (upload.CatalogFileId.HasValue && fileIds.Contains(upload.CatalogFileId.Value)) upload.CatalogFileId = null;
        }
        dbContext.UploadSessions.RemoveRange(uploads.Where(upload => upload.State != "completed"));
        dbContext.Files.RemoveRange(trashedFiles);
        await dbContext.SaveChangesAsync(cancellationToken);

        var remainingFolders = trashedFolders.ToDictionary(folder => folder.Id);
        while (remainingFolders.Count > 0)
        {
            var leaves = remainingFolders.Values
                .Where(folder => !remainingFolders.Values.Any(candidate => candidate.ParentFolderId == folder.Id))
                .ToArray();
            if (leaves.Length == 0) return MediaErrors.Conflict("Trash contains an invalid folder hierarchy that cannot be cleared safely.");
            dbContext.Folders.RemoveRange(leaves);
            await dbContext.SaveChangesAsync(cancellationToken);
            foreach (var leaf in leaves) remainingFolders.Remove(leaf.Id);
        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateStorageSummary(userId);
        foreach (var stagingPath in cancelledUploadStagingFiles)
        {
            try
            {
                if (File.Exists(stagingPath)) File.Delete(stagingPath);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "[media.trash] cancelled upload staging cleanup failed");
            }
        }
        var deletedOriginalCount = 0;
        long deletedOriginalBytes = 0;
        var failedOriginalCount = 0;
        using var objectStore = objectStoreFactory.Create();
        foreach (var queuedBlob in deletableBlobs)
        {
            queuedBlob.DeletionAttemptCount++;
            queuedBlob.DeletionLastAttemptAt = DateTime.UtcNow;
            var referencedBeforeStorageDelete = await dbContext.FileVersions.AsNoTracking()
                .AnyAsync(version => version.BlobSha256 == queuedBlob.Sha256, cancellationToken);
            if (referencedBeforeStorageDelete)
            {
                queuedBlob.DeletionError = "Storage deletion blocked because a catalog reference exists.";
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogCritical("[media.trash] permanent storage deletion blocked by final reference check");
                failedOriginalCount++;
                continue;
            }
            try
            {
                await objectStore.DeleteOriginalPermanentlyAsync(queuedBlob.Sha256, cancellationToken);
                await originalCache.RemoveAsync(queuedBlob.Sha256, cancellationToken);
                thumbnailCache.RemoveAll(queuedBlob.Sha256);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                queuedBlob.DeletionError = "Permanent storage deletion failed. Use Developer maintenance to review and retry.";
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogError("[media.trash] permanent storage deletion failed; cleanup remains pending");
                failedOriginalCount++;
                continue;
            }
            var stillReferenced = await dbContext.FileVersions.AsNoTracking()
                .AnyAsync(version => version.BlobSha256 == queuedBlob.Sha256, cancellationToken);
            if (stillReferenced)
            {
                queuedBlob.DeletionError = "A catalog reference appeared during permanent storage deletion.";
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogCritical("[media.trash] catalog reference appeared during permanent storage deletion");
                continue;
            }
            dbContext.Blobs.Remove(queuedBlob);
            await dbContext.SaveChangesAsync(cancellationToken);
            deletedOriginalCount++;
            deletedOriginalBytes += queuedBlob.SizeBytes;
        }
        logger.LogInformation(
            "[media.trash] cleared deletedFiles={DeletedFileCount} deletedFolders={DeletedFolderCount} deletedOriginals={DeletedOriginalCount} deletedOriginalBytes={DeletedOriginalBytes} failedOriginals={FailedOriginalCount} cancelledUploads={CancelledUploadCount}",
            trashedFiles.Count,
            trashedFolders.Count,
            deletedOriginalCount,
            deletedOriginalBytes,
            failedOriginalCount,
            cancelledUploadStagingFiles.Length);
        return new ClearTrashRes
        {
            DeletedFileCount = trashedFiles.Count,
            DeletedFolderCount = trashedFolders.Count,
            DeletedOriginalCount = deletedOriginalCount,
            DeletedOriginalBytes = deletedOriginalBytes,
            FailedOriginalCount = failedOriginalCount
        };
        }
        finally
        {
            OriginalMutationGate.Release();
        }
    }

    public async Task<Result<CreateUploadRes>> CreateUploadAsync(Guid userId, CreateUploadReq request, CancellationToken cancellationToken)
    {
        var nameResult = CatalogItemPolicy.NormalizeName(request.Name, "File");
        if (nameResult.IsFailure) return nameResult.ErrorValue;
        var name = nameResult.SuccessValue;
        if (request.SizeBytes < 0)
        {
            return MediaErrors.Validation("File sizes cannot be negative.");
        }
        if (request.ClientUploadId == Guid.Empty)
        {
            return MediaErrors.Validation("Client upload IDs must be non-empty UUIDs.");
        }

        var mimeType = string.IsNullOrWhiteSpace(request.MimeType) ? "application/octet-stream" : request.MimeType.Trim();
        if (mimeType.Length > 255) return MediaErrors.Validation("MIME types may not exceed 255 characters.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var parentId = await ResolveUploadParentIdAsync(
            library,
            request.ParentId,
            request.UseDefaultMediaFolder,
            request.UseDefaultDocumentsFolder,
            cancellationToken);
        if (parentId.IsFailure) return parentId.ErrorValue;
        if (request.ClientUploadId.HasValue)
        {
            var existing = await dbContext.UploadSessions.SingleOrDefaultAsync(
                upload => upload.Id == request.ClientUploadId && upload.UserId == userId,
                cancellationToken);
            if (existing is not null)
            {
                if (existing.LibraryId != library.Id
                    || existing.ParentFolderId != parentId.SuccessValue
                    || existing.Name != name
                    || existing.MimeType != mimeType
                    || existing.SizeBytes != request.SizeBytes)
                {
                    return MediaErrors.Conflict("The saved upload no longer matches its server session.");
                }
                return new CreateUploadRes
                {
                    Id = existing.Id,
                    ChunkSizeBytes = ChunkSizeBytes,
                    ReceivedBytes = existing.ReceivedBytes
                };
            }
            if (await dbContext.UploadSessions.AnyAsync(upload => upload.Id == request.ClientUploadId, cancellationToken))
                return MediaErrors.Conflict("The client upload ID is already in use.");
        }
        if (!request.AllowNameConflict
            && await dbContext.Files.AnyAsync(file => file.LibraryId == library.Id
                && file.ParentFolderId == parentId.SuccessValue
                && file.TrashedAt == null
                && file.Name == name,
                cancellationToken))
        {
            return MediaErrors.Conflict("An item with this name already exists.");
        }

        var upload = new UploadSession
        {
            Id = request.ClientUploadId ?? Guid.CreateVersion7(),
            UserId = userId,
            LibraryId = library.Id,
            ParentFolderId = parentId.SuccessValue,
            Name = name,
            MimeType = mimeType,
            SizeBytes = request.SizeBytes,
            StagingFileName = $"{Guid.CreateVersion7():N}.part"
        };
        dbContext.UploadSessions.Add(upload);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        Directory.CreateDirectory(_stagingDirectory);
        await using (File.Create(StagingPath(upload))) { }
        logger.LogInformation(
            "[media.upload] session created uploadId={UploadId} sizeBytes={SizeBytes} parentId={ParentId}",
            upload.Id,
            upload.SizeBytes,
            upload.ParentFolderId);
        return new CreateUploadRes { Id = upload.Id, ChunkSizeBytes = ChunkSizeBytes, ReceivedBytes = 0 };
    }

    public async Task<Result<UploadSessionRes>> GetUploadSessionAsync(
        Guid userId,
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var uploadResult = await GetUploadAsync(userId, uploadId, cancellationToken);
        if (uploadResult.IsFailure) return uploadResult.ErrorValue;
        var upload = uploadResult.SuccessValue;
        return new UploadSessionRes
        {
            Id = upload.Id,
            ChunkSizeBytes = ChunkSizeBytes,
            ReceivedBytes = upload.ReceivedBytes,
            SizeBytes = upload.SizeBytes,
            State = upload.State
        };
    }

    private async Task<Result<Guid?>> ResolveUploadParentIdAsync(
        Library library,
        Guid? requestedParentId,
        bool useDefaultMediaFolder,
        bool useDefaultDocumentsFolder,
        CancellationToken cancellationToken)
    {
        if (useDefaultMediaFolder && useDefaultDocumentsFolder)
        {
            return MediaErrors.Validation("Choose only one default upload folder.");
        }

        var parentId = requestedParentId;
        if (!parentId.HasValue && useDefaultMediaFolder)
        {
            parentId = await libraryBootstrapper.SystemFolderIdAsync(library.Id, LibraryBootstrapper.MediaFolderRole, cancellationToken);
        }
        else if (!parentId.HasValue)
        {
            parentId = await libraryBootstrapper.SystemFolderIdAsync(library.Id, LibraryBootstrapper.DocumentsFolderRole, cancellationToken);
        }

        var destination = await libraryBootstrapper.EnsureParentAsync(library.Id, parentId, requireActive: false, cancellationToken);
        return destination.IsFailure ? destination.ErrorValue : parentId;
    }

    public async Task<Result<UploadProgressRes>> AppendUploadChunkAsync(Guid userId, Guid uploadId, long start, long end, long total, Stream content, CancellationToken cancellationToken)
    {
        var uploadResult = await GetPendingUploadAsync(userId, uploadId, cancellationToken);
        if (uploadResult.IsFailure) return uploadResult.ErrorValue;
        var upload = uploadResult.SuccessValue;
        if (total != upload.SizeBytes || start != upload.ReceivedBytes || end < start || end >= total)
        {
            return MediaErrors.Validation("The upload chunk range does not match the current upload state.");
        }

        var expectedLength = end - start + 1;
        if (expectedLength > ChunkSizeBytes || expectedLength > upload.SizeBytes - upload.ReceivedBytes)
        {
            return MediaErrors.Validation("The upload chunk is too large.");
        }

        var path = StagingPath(upload);
        if (!File.Exists(path)) return MediaErrors.Conflict("The staged upload is no longer available. Start a new upload.");
        await using (var destination = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await content.CopyToAsync(destination, cancellationToken);
            if (destination.Length != end + 1)
            {
                return MediaErrors.Validation("The upload chunk length does not match its Content-Range header.");
            }
        }

        upload.ReceivedBytes += expectedLength;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new UploadProgressRes { Id = upload.Id, ReceivedBytes = upload.ReceivedBytes, SizeBytes = upload.SizeBytes };
    }

    public async Task<Result<CompleteUploadRes>> CompleteUploadAsync(Guid userId, Guid uploadId, CompleteUploadReq request, CancellationToken cancellationToken)
    {
        var uploadResult = await GetUploadAsync(userId, uploadId, cancellationToken);
        if (uploadResult.IsFailure) return uploadResult.ErrorValue;
        var upload = uploadResult.SuccessValue;
        if (string.Equals(upload.State, "completed", StringComparison.Ordinal) && upload.CatalogFileId.HasValue)
        {
            var completedFile = await dbContext.Files.AsNoTracking().SingleAsync(file => file.Id == upload.CatalogFileId, cancellationToken);
            logger.LogInformation(
                "[media.upload] completion reused uploadId={UploadId} itemId={ItemId} reason=idempotent_retry",
                upload.Id,
                completedFile.Id);
            metadataWorkSignal.Signal();
            return new CompleteUploadRes
            {
                Item = ToFileResponse(completedFile),
                DuplicateState = await DuplicateStateAsync(upload.LibraryId, completedFile.CurrentVersionId, completedFile.Id, cancellationToken)
            };
        }
        if (!string.Equals(upload.State, "pending", StringComparison.Ordinal)) return MediaErrors.Conflict("The upload cannot be completed.");
        if (upload.ReceivedBytes != upload.SizeBytes) return MediaErrors.Conflict("Upload is incomplete.");

        var requestedHash = request.Sha256?.Trim().ToLowerInvariant();
        if (requestedHash is null || requestedHash.Length != 64 || requestedHash.Any(character => !Uri.IsHexDigit(character)))
        {
            return MediaErrors.Validation("A lowercase SHA-256 file checksum is required to complete an upload.");
        }

        var path = StagingPath(upload);
        if (!File.Exists(path)) return MediaErrors.Conflict("The staged upload is no longer available. Start a new upload.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var computedHash = await UploadHash.Sha256Async(stream, cancellationToken);
        if (!string.Equals(computedHash, requestedHash, StringComparison.Ordinal)) return MediaErrors.Validation("The uploaded bytes did not match the supplied SHA-256 checksum.");
        logger.LogInformation("[media.upload] checksum verified uploadId={UploadId} sizeBytes={SizeBytes}", upload.Id, upload.SizeBytes);
        var uploaderEmail = await uploaderIdentityDirectory.GetEmailAsync(userId, cancellationToken);

        await OriginalMutationGate.WaitAsync(cancellationToken);
        try
        {
        if (!request.AllowNameConflict
            && await dbContext.Files.AnyAsync(file => file.LibraryId == upload.LibraryId
                && file.ParentFolderId == upload.ParentFolderId
                && file.TrashedAt == null
                && file.Name == upload.Name,
                cancellationToken))
        {
            return MediaErrors.Conflict("An item with this name already exists.");
        }
        var duplicateState = await DuplicateStateByHashAsync(upload.LibraryId, computedHash, null, cancellationToken);
        var now = DateTime.UtcNow;
        using var objectStore = objectStoreFactory.Create();
        logger.LogInformation("[media.s3] store original requested uploadId={UploadId} sizeBytes={SizeBytes}", upload.Id, upload.SizeBytes);
        await objectStore.PutOriginalIfMissingAsync(computedHash, stream, upload.SizeBytes, upload.MimeType, upload.Name, userId, uploaderEmail, now, cancellationToken);
        logger.LogInformation("[media.s3] store original completed uploadId={UploadId} sizeBytes={SizeBytes}", upload.Id, upload.SizeBytes);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var blob = await dbContext.Blobs.SingleOrDefaultAsync(candidate => candidate.Sha256 == computedHash, cancellationToken);
        if (blob is null)
        {
            blob = new Blob { Sha256 = computedHash, ObjectKey = S3ObjectStore.BlobKey(computedHash), SizeBytes = upload.SizeBytes, CreatedAt = now };
            dbContext.Blobs.Add(blob);
        }
        else if (blob.LifecycleState == Blob.PendingPermanentDeletionLifecycleState)
        {
            return MediaErrors.Conflict("This stored original is being permanently deleted. Retry the upload after storage cleanup finishes.");
        }

        var library = await dbContext.Libraries.SingleAsync(candidate => candidate.Id == upload.LibraryId, cancellationToken);
        var version = new FileVersion
        {
            BlobSha256 = computedHash,
            SizeBytes = upload.SizeBytes,
            MimeType = upload.MimeType,
            DeclaredMimeType = upload.MimeType,
            OriginalName = upload.Name,
            UploaderUserId = userId,
            CreatedAt = now
        };
        var file = new CatalogFile
        {
            LibraryId = library.Id,
            ParentFolderId = upload.ParentFolderId,
            Name = upload.Name,
            MimeType = upload.MimeType,
            SizeBytes = upload.SizeBytes,
            Sequence = ++library.NextItemSequence,
            CreatedAt = now,
            ModifiedAt = now,
            CapturedAt = now,
            CaptureDateSource = "uploaded_at",
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
            EffectiveCapturedAt = now,
            CaptureDateSource = "uploaded_at",
            RecoveryManifestState = "pending"
        });
        upload.CatalogFileId = file.Id;
        upload.State = "completed";
        upload.CompletedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        InvalidateStorageSummary(userId);
        logger.LogInformation(
            "[media.catalog] upload committed uploadId={UploadId} itemId={ItemId} parentId={ParentId}",
            upload.Id,
            file.Id,
            file.ParentFolderId);

        metadataWorkSignal.Signal();
        logger.LogInformation("[media.upload] completion finished uploadId={UploadId} itemId={ItemId} metadataState=queued", upload.Id, file.Id);
        return new CompleteUploadRes
        {
            Item = ToFileResponse(file),
            DuplicateState = duplicateState
        };
        }
        finally
        {
            OriginalMutationGate.Release();
        }
    }

    public async Task<Result> CancelUploadAsync(Guid userId, Guid uploadId, CancellationToken cancellationToken)
    {
        var uploadResult = await GetUploadAsync(userId, uploadId, cancellationToken);
        if (uploadResult.IsFailure) return uploadResult.ErrorValue;
        var upload = uploadResult.SuccessValue;
        if (!string.Equals(upload.State, "pending", StringComparison.Ordinal))
            return MediaErrors.Conflict("A completed upload cannot be cancelled.");

        var path = StagingPath(upload);
        if (File.Exists(path)) File.Delete(path);
        dbContext.UploadSessions.Remove(upload);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.upload] session cancelled uploadId={UploadId} receivedBytes={ReceivedBytes}", upload.Id, upload.ReceivedBytes);
        return Result.Success();
    }

    private async Task<string> DuplicateStateAsync(
        Guid libraryId,
        Guid currentVersionId,
        Guid excludeFileId,
        CancellationToken cancellationToken)
    {
        var hash = await dbContext.FileVersions.AsNoTracking()
            .Where(version => version.Id == currentVersionId)
            .Select(version => version.BlobSha256)
            .SingleAsync(cancellationToken);
        return await DuplicateStateByHashAsync(libraryId, hash, excludeFileId, cancellationToken);
    }

    private async Task<string> DuplicateStateByHashAsync(
        Guid libraryId,
        string hash,
        Guid? excludeFileId,
        CancellationToken cancellationToken)
    {
        var matches = await (
            from file in dbContext.Files.AsNoTracking()
            join version in dbContext.FileVersions.AsNoTracking() on file.CurrentVersionId equals version.Id
            where file.LibraryId == libraryId
                && version.BlobSha256 == hash
                && (!excludeFileId.HasValue || file.Id != excludeFileId)
            select file.TrashedAt)
            .ToListAsync(cancellationToken);
        return matches.Any(trashedAt => trashedAt == null)
            ? "active"
            : matches.Count > 0
                ? "trashed"
                : "none";
    }

    public async Task<Result<MediaItemRes>> RetryMetadataAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var file = await (
            from candidate in dbContext.Files
            join library in dbContext.Libraries on candidate.LibraryId equals library.Id
            where candidate.Id == itemId && library.OwnerUserId == userId
            select candidate).SingleOrDefaultAsync(cancellationToken);
        if (file is null) return MediaErrors.NotFound("The requested file was not found in your library.");
        await metadataProcessor.QueueRetryAsync(file.CurrentVersionId, cancellationToken);
        logger.LogInformation("[media.metadata] manual retry requested itemId={ItemId}", file.Id);
        metadataWorkSignal.Signal();
        return await ToFileResponseAsync(userId, file, cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "MediaBinaryContent owns and disposes the selected local or S3 stream and provider lifetime.")]
    public async Task<Result<MediaBinaryContent>> OpenContentAsync(Guid userId, Guid itemId, long? rangeStart, long? rangeEnd, CancellationToken cancellationToken)
    {
        var fileResult = await GetFileBlobAsync(userId, itemId, cancellationToken);
        if (fileResult.IsFailure) return fileResult.ErrorValue;
        var file = fileResult.SuccessValue;
        if ((rangeStart.HasValue && (rangeStart.Value < 0 || rangeStart.Value >= file.SizeBytes))
            || (rangeEnd.HasValue && rangeStart.HasValue && rangeEnd.Value < rangeStart.Value))
        {
            return MediaErrors.Validation("The requested byte range is not valid for this file.");
        }

        long? boundedEnd = rangeEnd.HasValue ? Math.Min(rangeEnd.Value, file.SizeBytes - 1) : null;
        return await OpenFileContentAsync(
            file,
            rangeStart,
            boundedEnd,
            OriginalCacheAdmission.Automatic,
            cancellationToken);
    }

    public async Task<Result<MediaBinaryInfo>> GetContentInfoAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var fileResult = await GetFileBlobAsync(userId, itemId, cancellationToken);
        if (fileResult.IsFailure) return fileResult.ErrorValue;
        var file = fileResult.SuccessValue;
        return new MediaBinaryInfo(file.MimeType, file.SizeBytes, file.VersionCreatedAt, file.BlobSha256);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "MediaBinaryContent owns the selected local or S3 stream and provider lifetime.")]
    private async Task<MediaBinaryContent> OpenFileContentAsync(
        FileBlob file,
        long? rangeStart,
        long? boundedEnd,
        OriginalCacheAdmission admission,
        CancellationToken cancellationToken)
    {
        var effectiveStart = rangeStart ?? 0;
        var effectiveEnd = boundedEnd ?? (file.SizeBytes - 1);
        var requestedLength = effectiveEnd - effectiveStart + 1;
        var cached = await originalCache.TryOpenReadAsync(file.BlobSha256, file.SizeBytes, cancellationToken);
        if (cached is not null)
        {
            logger.LogInformation(
                "[media.cache] original hit itemId={ItemId} sizeBytes={SizeBytes} rangeStart={RangeStart} rangeEnd={RangeEnd}",
                file.Id,
                file.SizeBytes,
                rangeStart,
                boundedEnd);
            if (effectiveStart > 0) cached.Seek(effectiveStart, SeekOrigin.Begin);
            Stream local = requestedLength == file.SizeBytes
                ? cached
                : new LengthLimitedReadStream(cached, requestedLength);
            return new MediaBinaryContent(
                local,
                file.MimeType,
                requestedLength,
                file.SizeBytes,
                file.VersionCreatedAt,
                file.BlobSha256,
                NullDisposable.Instance);
        }

        logger.LogInformation(
            "[media.cache] original miss itemId={ItemId} sizeBytes={SizeBytes} rangeStart={RangeStart} rangeEnd={RangeEnd}",
            file.Id,
            file.SizeBytes,
            rangeStart,
            boundedEnd);
        logger.LogInformation(
            "[media.s3] get original requested itemId={ItemId} reason={Reason} rangeStart={RangeStart} rangeEnd={RangeEnd}",
            file.Id,
            rangeStart.HasValue ? "range_read" : "full_read",
            rangeStart,
            boundedEnd);
        var objectStore = objectStoreFactory.Create();
        try
        {
            var remote = await objectStore.OpenOriginalAsync(file.BlobSha256, rangeStart, boundedEnd, cancellationToken);
            Stream content = remote.Content;
            if (effectiveStart == 0 && effectiveEnd == file.SizeBytes - 1)
            {
                IOriginalCachePopulation? population = null;
                try
                {
                    population = await originalCache.TryBeginPopulationAsync(
                        file.BlobSha256,
                        file.SizeBytes,
                        admission,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "[media.cache] cache-through unavailable itemId={ItemId} exceptionType={ExceptionType}",
                        file.Id,
                        exception.GetType().Name);
                }
                if (population is not null)
                {
                    logger.LogInformation(
                        "[media.cache] cache-through started itemId={ItemId} sizeBytes={SizeBytes} admission={Admission}",
                        file.Id,
                        file.SizeBytes,
                        admission);
                    content = new CacheThroughReadStream(content, population, logger, file.Id);
                }
            }
            logger.LogInformation(
                "[media.s3] get original opened itemId={ItemId} reason={Reason} sizeBytes={SizeBytes}",
                file.Id,
                rangeStart.HasValue ? "range_read" : "full_read",
                remote.ContentLength);
            return new MediaBinaryContent(
                content,
                file.MimeType,
                remote.ContentLength,
                file.SizeBytes,
                file.VersionCreatedAt,
                file.BlobSha256,
                new CompositeDisposable(remote, objectStore));
        }
        catch { objectStore.Dispose(); throw; }
    }

    public async Task<Result<DownloadPreviewRes>> PreviewDownloadAsync(
        Guid userId,
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken) =>
        ToDownloadPreview(await PrepareDownloadAsync(userId, itemIds, cancellationToken));

    public async Task<Result<DownloadPreviewRes>> PreviewFolderDownloadAsync(
        Guid userId,
        Guid folderId,
        CancellationToken cancellationToken) =>
        ToDownloadPreview(await PrepareFolderDownloadAsync(userId, folderId, cancellationToken));

    public async Task<Result<DownloadPreviewRes>> PreviewAlbumDownloadAsync(
        Guid userId,
        Guid albumId,
        CancellationToken cancellationToken) =>
        ToDownloadPreview(await PrepareAlbumDownloadAsync(userId, albumId, cancellationToken));

    private static Result<DownloadPreviewRes> ToDownloadPreview(Result<MediaDownloadContent> result) =>
        result.IsFailure
            ? result.ErrorValue
            : new DownloadPreviewRes
            {
                FileCount = result.SuccessValue.FileCount,
                TotalBytes = result.SuccessValue.TotalBytes
            };

    public async Task<Result<MediaDownloadContent>> PrepareDownloadAsync(
        Guid userId,
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        var distinctIds = itemIds.Distinct().ToArray();
        if (distinctIds.Length == 0) return MediaErrors.Validation("Select at least one item to download.");
        if (distinctIds.Length > 100) return MediaErrors.Validation("Download no more than 100 items at once.");

        var folderIds = await dbContext.Folders.AsNoTracking()
            .Where(folder => distinctIds.Contains(folder.Id) && folder.TrashedAt == null)
            .Select(folder => folder.Id)
            .ToHashSetAsync(cancellationToken);
        var files = new List<SelectedDownloadFile>(distinctIds.Length);
        var folders = new List<SelectedDownloadFolder>(folderIds.Count);
        for (var index = 0; index < distinctIds.Length; index++)
        {
            var itemId = distinctIds[index];
            if (folderIds.Contains(itemId))
            {
                var folderResult = await PrepareFolderArchiveSourceAsync(userId, itemId, cancellationToken);
                if (folderResult.IsFailure) return folderResult.ErrorValue;
                folders.Add(new SelectedDownloadFolder(index, folderResult.SuccessValue));
                continue;
            }

            var fileResult = await GetFileBlobAsync(userId, itemId, cancellationToken);
            if (fileResult.IsFailure) return fileResult.ErrorValue;
            files.Add(new SelectedDownloadFile(index, fileResult.SuccessValue));
        }

        if (folders.Count == 0 && files.Count == 1)
        {
            var file = files[0].File;
            return new MediaDownloadContent(
                SafeDownloadName(file.Name),
                file.MimeType,
                file.SizeBytes,
                fileCount: 1,
                totalBytes: file.SizeBytes,
                (destination, token) => WriteDownloadFileAsync(userId, file.Id, destination, token));
        }

        var rootFolders = folders
            .Where(folder => !folders.Any(other =>
                other.Source.Id != folder.Source.Id
                && other.Source.FolderIds.Contains(folder.Source.Id)))
            .ToList();
        var filesInSelectedFolders = rootFolders
            .SelectMany(folder => folder.Source.Plan.Files)
            .Select(file => file.Id)
            .ToHashSet();
        var rootFiles = files.Where(file => !filesInSelectedFolders.Contains(file.File.Id)).ToList();

        if (rootFolders.Count == 1 && rootFiles.Count == 0)
        {
            var folder = rootFolders[0].Source;
            return CreateArchiveDownload(userId, folder.Name, folder.Plan);
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        var archiveFiles = new List<ArchiveEntryFile>();
        foreach (var item in rootFolders
                     .Select(folder => new SelectedArchiveItem(folder.Index, Folder: folder.Source, File: null))
                     .Concat(rootFiles.Select(file => new SelectedArchiveItem(file.Index, Folder: null, File: file.File)))
                     .OrderBy(item => item.Index))
        {
            if (item.Folder is { } folder)
            {
                var rootName = UniqueDownloadName(SafeArchiveSegment(folder.Name, "folder"), usedNames);
                var rebased = RebaseArchivePlan(folder.Plan, rootName);
                directories.AddRange(rebased.Directories);
                archiveFiles.AddRange(rebased.Files);
            }
            else if (item.File is { } file)
            {
                archiveFiles.Add(new ArchiveEntryFile(
                    file.Id,
                    UniqueDownloadName(SafeArchiveSegment(file.Name, "file"), usedNames),
                    file.SizeBytes));
            }
        }

        return CreateArchiveDownload(
            userId,
            $"media-explorer-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            new ArchivePlan(directories, archiveFiles));
    }

    public async Task<Result<MediaDownloadContent>> PrepareFolderDownloadAsync(
        Guid userId,
        Guid folderId,
        CancellationToken cancellationToken)
    {
        var sourceResult = await PrepareFolderArchiveSourceAsync(userId, folderId, cancellationToken);
        if (sourceResult.IsFailure) return sourceResult.ErrorValue;
        var source = sourceResult.SuccessValue;
        return CreateArchiveDownload(userId, source.Name, source.Plan);
    }

    private async Task<Result<FolderArchiveSource>> PrepareFolderArchiveSourceAsync(
        Guid userId,
        Guid folderId,
        CancellationToken cancellationToken)
    {
        var access = await authorization.FolderAccessAsync(userId, folderId, requireActive: true, cancellationToken);
        if (access is null) return MediaErrors.NotFound("The folder was not found.");

        var folders = await dbContext.Folders.AsNoTracking()
            .Where(folder => folder.LibraryId == access.LibraryId && folder.TrashedAt == null)
            .Select(folder => new ArchiveFolder(folder.Id, folder.ParentFolderId, folder.Name, folder.Sequence))
            .ToListAsync(cancellationToken);
        var root = folders.SingleOrDefault(folder => folder.Id == folderId);
        if (root is null) return MediaErrors.NotFound("The folder was not found.");

        var children = folders.ToLookup(folder => folder.ParentFolderId);
        var descendantIds = new HashSet<Guid> { folderId };
        var pending = new Queue<Guid>();
        pending.Enqueue(folderId);
        while (pending.TryDequeue(out var parentId))
        {
            foreach (var child in children[parentId])
            {
                if (descendantIds.Add(child.Id)) pending.Enqueue(child.Id);
            }
        }

        var files = await (
            from file in dbContext.Files.AsNoTracking()
            where file.LibraryId == access.LibraryId
                && file.TrashedAt == null
                && file.ParentFolderId.HasValue
                && descendantIds.Contains(file.ParentFolderId.Value)
            orderby file.Sequence, file.Id
            select new ArchiveCatalogFile(file.Id, file.ParentFolderId!.Value, file.Name, file.Sequence, file.SizeBytes))
            .ToListAsync(cancellationToken);
        var plan = BuildFolderArchivePlan(root, folders.Where(folder => descendantIds.Contains(folder.Id)), files);
        logger.LogInformation(
            "[media.download] folder archive prepared folderId={FolderId} folderCount={FolderCount} fileCount={FileCount}",
            folderId,
            plan.Directories.Count,
            plan.Files.Count);
        return new FolderArchiveSource(folderId, root.Name, descendantIds, plan);
    }

    public async Task<Result<MediaDownloadContent>> PrepareAlbumDownloadAsync(
        Guid userId,
        Guid albumId,
        CancellationToken cancellationToken)
    {
        var album = await (
            from candidate in dbContext.Albums.AsNoTracking()
            join library in dbContext.Libraries.AsNoTracking() on candidate.LibraryId equals library.Id
            where candidate.Id == albumId
                && (library.OwnerUserId == userId
                    || dbContext.CatalogShares.AsNoTracking().Any(share =>
                        share.AlbumId == candidate.Id
                        && share.RecipientUserId == userId
                        && share.Role == CatalogShare.ViewerRole))
            select new { candidate.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (album is null) return MediaErrors.NotFound("The album was not found.");

        var files = await (
            from membership in dbContext.AlbumItems.AsNoTracking()
            join file in dbContext.Files.AsNoTracking() on membership.FileId equals file.Id
            where membership.AlbumId == albumId && file.TrashedAt == null
            orderby membership.AddedAt, membership.FileId
            select new ArchiveCatalogFile(file.Id, albumId, file.Name, file.Sequence, file.SizeBytes))
            .ToListAsync(cancellationToken);
        var rootName = SafeArchiveSegment(album.Name, "album");
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new ArchivePlan(
            [$"{rootName}/"],
            files.Select(file => new ArchiveEntryFile(
                    file.Id,
                    $"{rootName}/{UniqueDownloadName(SafeArchiveSegment(file.Name, "file"), usedNames)}",
                    file.SizeBytes))
                .ToList());
        logger.LogInformation(
            "[media.download] album archive prepared albumId={AlbumId} fileCount={FileCount}",
            albumId,
            plan.Files.Count);
        return CreateArchiveDownload(userId, album.Name, plan);
    }

    private static ArchivePlan BuildFolderArchivePlan(
        ArchiveFolder root,
        IEnumerable<ArchiveFolder> folders,
        IReadOnlyCollection<ArchiveCatalogFile> files)
    {
        var folderList = folders.ToList();
        var children = folderList.ToLookup(folder => folder.ParentFolderId);
        var filesByParent = files.ToLookup(file => file.ParentFolderId);
        var paths = new Dictionary<Guid, string>
        {
            [root.Id] = SafeArchiveSegment(root.Name, "folder")
        };
        var directories = new List<string>();
        var archiveFiles = new List<ArchiveEntryFile>(files.Count);
        var pending = new Queue<Guid>();
        pending.Enqueue(root.Id);

        while (pending.TryDequeue(out var folderId))
        {
            var path = paths[folderId];
            directories.Add($"{path}/");
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in children[folderId].OrderBy(folder => folder.Sequence).ThenBy(folder => folder.Id))
            {
                var segment = UniqueDownloadName(SafeArchiveSegment(child.Name, "folder"), usedNames);
                paths[child.Id] = $"{path}/{segment}";
                pending.Enqueue(child.Id);
            }
            foreach (var file in filesByParent[folderId].OrderBy(file => file.Sequence).ThenBy(file => file.Id))
            {
                var segment = UniqueDownloadName(SafeArchiveSegment(file.Name, "file"), usedNames);
                archiveFiles.Add(new ArchiveEntryFile(file.Id, $"{path}/{segment}", file.SizeBytes));
            }
        }

        return new ArchivePlan(directories, archiveFiles);
    }

    private static ArchivePlan RebaseArchivePlan(ArchivePlan plan, string rootName)
    {
        var originalRoot = plan.Directories[0].TrimEnd('/');
        if (string.Equals(originalRoot, rootName, StringComparison.Ordinal)) return plan;
        var prefixLength = originalRoot.Length;
        return new ArchivePlan(
            plan.Directories.Select(path => $"{rootName}{path[prefixLength..]}").ToList(),
            plan.Files.Select(file => new ArchiveEntryFile(file.Id, $"{rootName}{file.EntryName[prefixLength..]}", file.SizeBytes)).ToList());
    }

    private MediaDownloadContent CreateArchiveDownload(Guid userId, string sourceName, ArchivePlan plan) =>
        new(
            $"{SafeArchiveSegment(sourceName, "media-export")}.zip",
            "application/zip",
            contentLength: null,
            fileCount: plan.Files.Count,
            totalBytes: plan.Files.Sum(file => file.SizeBytes),
            async (destination, token) =>
            {
                using var zipDestination = new AsyncZipWriteStream(destination);
                await using (var archive = new ZipArchive(zipDestination, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var directory in plan.Directories)
                    {
                        token.ThrowIfCancellationRequested();
                        archive.CreateEntry(directory, CompressionLevel.NoCompression);
                    }
                    foreach (var file in plan.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(file.EntryName, CompressionLevel.NoCompression);
                        await using var entryStream = await entry.OpenAsync(token);
                        await WriteDownloadFileAsync(userId, file.Id, entryStream, token);
                    }
                }
                await zipDestination.CompleteAsync(token);
            });

    public async Task<Result<MediaBinaryContent>> OpenThumbnailAsync(Guid userId, Guid itemId, string size, CancellationToken cancellationToken)
    {
        var fileResult = await GetFileBlobAsync(userId, itemId, cancellationToken, allowOwnerTrashed: true);
        if (fileResult.IsFailure) return fileResult.ErrorValue;
        var file = fileResult.SuccessValue;
        if (!IsMedia(file.MimeType))
        {
            return MediaErrors.Validation("Thumbnails are available for images and videos only.");
        }

        var pixelsResult = ThumbnailPixels(size);
        if (pixelsResult.IsFailure) return pixelsResult.ErrorValue;
        var pixels = pixelsResult.SuccessValue;
        var thumbnailPath = thumbnailCache.PathFor(file.BlobSha256, pixels);
        if (thumbnailCache.Exists(file.BlobSha256, pixels))
        {
            logger.LogInformation("[media.thumbnail] cache hit itemId={ItemId} size={ThumbnailSize} pixels={Pixels}", itemId, size, pixels);
        }
        else
        {
            logger.LogInformation("[media.thumbnail] cache miss itemId={ItemId} size={ThumbnailSize} pixels={Pixels}", itemId, size, pixels);
            logger.LogInformation("[media.thumbnail] generation started itemId={ItemId} pixels={Pixels}", itemId, pixels);
            await thumbnailCache.EnsureAsync(
                file.BlobSha256,
                file.MimeType,
                pixels,
                token => EnsureOriginalCachedAsync(file, token),
                cancellationToken);
            logger.LogInformation("[media.thumbnail] generation completed itemId={ItemId} pixels={Pixels}", itemId, pixels);
        }

        var info = new FileInfo(thumbnailPath);
        var stream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return new MediaBinaryContent(stream, "image/webp", info.Length, info.Length, info.LastWriteTimeUtc, $"{file.BlobSha256}-{pixels}", NullDisposable.Instance);
    }

    private async Task<Result<UploadSession>> GetPendingUploadAsync(Guid userId, Guid uploadId, CancellationToken cancellationToken)
    {
        var uploadResult = await GetUploadAsync(userId, uploadId, cancellationToken);
        if (uploadResult.IsFailure) return uploadResult.ErrorValue;
        var upload = uploadResult.SuccessValue;
        if (!string.Equals(upload.State, "pending", StringComparison.Ordinal)) return MediaErrors.Conflict("The upload has already completed or cannot be resumed.");
        return upload;
    }

    private async Task<Result<UploadSession>> GetUploadAsync(Guid userId, Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await dbContext.UploadSessions.SingleOrDefaultAsync(candidate => candidate.Id == uploadId && candidate.UserId == userId, cancellationToken);
        return upload is null ? MediaErrors.NotFound("The upload was not found.") : upload;
    }

    private async Task<Result<FileBlob>> GetFileBlobAsync(
        Guid userId,
        Guid itemId,
        CancellationToken cancellationToken,
        bool allowOwnerTrashed = false)
    {
        var access = await authorization.FileAccessAsync(userId, itemId, requireActive: true, cancellationToken);
        if (access is null && allowOwnerTrashed)
        {
            access = await authorization.FileAccessAsync(userId, itemId, requireActive: false, cancellationToken);
        }
        if (access is null) return MediaErrors.NotFound("The requested file was not found in your library.");
        var file = await (
            from catalogFile in dbContext.Files.AsNoTracking()
            join version in dbContext.FileVersions.AsNoTracking() on catalogFile.CurrentVersionId equals version.Id
            where catalogFile.Id == itemId
            select new FileBlob(
                catalogFile.Id,
                catalogFile.Name,
                catalogFile.MimeType,
                catalogFile.SizeBytes,
                version.BlobSha256,
                version.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
        return file is null ? MediaErrors.NotFound("The requested file was not found in your library.") : file;
    }

    private async Task WriteDownloadFileAsync(Guid userId, Guid itemId, Stream destination, CancellationToken cancellationToken)
    {
        var fileResult = await GetFileBlobAsync(userId, itemId, cancellationToken);
        if (fileResult.IsFailure)
        {
            throw new FileNotFoundException(fileResult.ErrorValue.Message);
        }
        using var content = await OpenFileContentAsync(
            fileResult.SuccessValue,
            rangeStart: null,
            boundedEnd: null,
            OriginalCacheAdmission.CompleteTransfer,
            cancellationToken);
        await content.Content.CopyToAsync(destination, cancellationToken);
    }

    private static string SafeDownloadName(string name)
    {
        var fileName = Path.GetFileName(name).Trim();
        var sanitized = new string(fileName.Select(character =>
            character is '/' or '\\' || char.IsControl(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "download" : sanitized;
    }

    private static string SafeArchiveSegment(string name, string fallback)
    {
        var sanitized = new string(name.Trim().Select(character =>
            character is '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*'
                || char.IsControl(character)
                ? '_'
                : character).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".." ? fallback : sanitized;
    }

    private static string UniqueDownloadName(string fileName, HashSet<string> usedNames)
    {
        if (usedNames.Add(fileName)) return fileName;
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (usedNames.Add(candidate)) return candidate;
        }
    }

    private async Task<string> EnsureOriginalCachedAsync(FileBlob file, CancellationToken cancellationToken)
    {
        if (await originalCache.TryOpenReadAsync(file.BlobSha256, file.SizeBytes, cancellationToken) is { } cached)
        {
            await cached.DisposeAsync();
            logger.LogInformation("[media.cache] thumbnail source hit itemId={ItemId} sizeBytes={SizeBytes}", file.Id, file.SizeBytes);
            return originalCache.PathFor(file.BlobSha256);
        }

        logger.LogInformation("[media.cache] thumbnail source miss itemId={ItemId} sizeBytes={SizeBytes}", file.Id, file.SizeBytes);
        await originalCache.EnsureAsync(file.BlobSha256, file.SizeBytes, async (destination, token) =>
        {
            logger.LogInformation("[media.s3] get original requested itemId={ItemId} reason=thumbnail_source", file.Id);
            using var objectStore = objectStoreFactory.Create();
            using var remote = await objectStore.OpenOriginalAsync(file.BlobSha256, null, null, token);
            await remote.Content.CopyToAsync(destination, token);
            logger.LogInformation("[media.s3] get original completed itemId={ItemId} reason=thumbnail_source sizeBytes={SizeBytes}", file.Id, remote.ContentLength);
        }, cancellationToken);
        return originalCache.PathFor(file.BlobSha256);
    }

    private async Task<bool> WouldCreateFolderCycleAsync(Guid libraryId, Guid folderId, Guid? parentId, CancellationToken cancellationToken)
    {
        var candidateParentId = parentId;
        while (candidateParentId.HasValue)
        {
            if (candidateParentId == folderId) return true;
            candidateParentId = await dbContext.Folders.AsNoTracking()
                .Where(folder => folder.Id == candidateParentId && folder.LibraryId == libraryId)
                .Select(folder => folder.ParentFolderId)
                .SingleOrDefaultAsync(cancellationToken);
        }
        return false;
    }

    private async Task<HashSet<Guid>> DescendantFolderIdsAsync(Guid libraryId, Guid rootId, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid> { rootId };
        var frontier = new[] { rootId };
        while (frontier.Length > 0)
        {
            var children = await dbContext.Folders.AsNoTracking()
                .Where(folder => folder.LibraryId == libraryId && folder.TrashedAt == null && folder.ParentFolderId.HasValue && frontier.Contains(folder.ParentFolderId.Value))
                .Select(folder => folder.Id)
                .ToArrayAsync(cancellationToken);
            foreach (var child in children) ids.Add(child);
            frontier = children;
        }
        return ids;
    }

    private string StagingPath(UploadSession upload) => Path.Combine(_stagingDirectory, upload.StagingFileName);

    internal static MediaItemRes ToFolderResponse(
        CatalogFolder folder,
        bool favorite = false,
        string? catalogPath = null,
        bool readOnly = false,
        string? sharedByDisplayName = null,
        DateTime? sharedAt = null,
        IReadOnlyList<TagSummaryRes>? tags = null) => new()
    {
        Id = folder.Id,
        Name = folder.Name,
        Kind = "folder",
        ParentId = folder.ParentFolderId,
        CreatedAt = folder.CreatedAt,
        ModifiedAt = folder.ModifiedAt,
        Favorite = favorite,
        LocalState = "remote",
        SyncState = "synced",
        CatalogPath = catalogPath,
        TrashedAt = folder.TrashedAt,
        SystemRole = folder.SystemRole,
        AccessRole = readOnly ? "viewer" : "owner",
        SharedByDisplayName = sharedByDisplayName,
        SharedAt = sharedAt,
        Tags = tags ?? [],
        Capabilities = readOnly
            ? ViewerCapabilities()
            : folder.SystemRole is null
            ? new ItemCapabilitiesRes()
            : new ItemCapabilitiesRes { CanRename = false, CanMove = false, CanTrash = false, CanShare = false }
    };

    private async Task<MediaItemRes> ToFileResponseAsync(
        Guid userId,
        CatalogFile file,
        CancellationToken cancellationToken,
        bool includeImportedDetails = false,
        bool readOnly = false)
    {
        var favorite = await dbContext.FileUserStates.AsNoTracking()
            .AnyAsync(state => state.UserId == userId && state.FileId == file.Id && state.Favorite, cancellationToken);
        var tags = (await TagsForItemsAsync([file.Id], [], cancellationToken)).GetValueOrDefault(file.Id);
        return ToFileResponse(file, favorite, includeImportedDetails: includeImportedDetails, readOnly: readOnly, tags: tags);
    }

    private async Task<MediaItemRes> ToFolderResponseAsync(Guid userId, CatalogFolder folder, CancellationToken cancellationToken, bool readOnly = false)
    {
        var favorite = await dbContext.FolderUserStates.AsNoTracking()
            .AnyAsync(state => state.UserId == userId && state.FolderId == folder.Id && state.Favorite, cancellationToken);
        var tags = (await TagsForItemsAsync([], [folder.Id], cancellationToken)).GetValueOrDefault(folder.Id);
        return ToFolderResponse(folder, favorite, readOnly: readOnly, tags: tags);
    }

    internal static MediaItemRes ToFileResponse(
        CatalogFile file,
        bool favorite = false,
        string? catalogPath = null,
        bool includeImportedDetails = false,
        bool readOnly = false,
        string? sharedByDisplayName = null,
        DateTime? sharedAt = null,
        IReadOnlyList<TagSummaryRes>? tags = null) => new()
    {
        Id = file.Id,
        Name = file.Name,
        Kind = KindFor(file.MimeType),
        MimeType = file.MimeType,
        SizeBytes = file.SizeBytes,
        ParentId = file.ParentFolderId,
        CreatedAt = file.CreatedAt,
        ModifiedAt = file.ModifiedAt,
        CapturedAt = file.CapturedAt,
        CaptureDateSource = file.CaptureDateSource,
        Metadata = ToMetadataResponse(file, includeImportedDetails),
        Favorite = favorite,
        ThumbnailPath = ThumbnailPathFor(file.Id, file.MimeType),
        LocalState = "remote",
        SyncState = "synced",
        CatalogPath = catalogPath,
        TrashedAt = file.TrashedAt,
        AccessRole = readOnly ? "viewer" : "owner",
        SharedByDisplayName = sharedByDisplayName,
        SharedAt = sharedAt,
        Tags = tags ?? [],
        Capabilities = readOnly ? ViewerCapabilities() : new ItemCapabilitiesRes()
    };

    private static ItemCapabilitiesRes ViewerCapabilities() => new()
    {
        CanRename = false,
        CanMove = false,
        CanTrash = false,
        CanFavorite = true,
        CanRetryMetadata = false,
        CanShare = false
    };

    private static MediaMetadataRes ToMetadataResponse(CatalogFile file, bool includeImportedDetails) => new()
    {
        State = file.MetadataState,
        AttemptCount = file.MetadataAttemptCount,
        ErrorCode = file.MetadataErrorCode,
        ErrorMessage = file.MetadataErrorMessage,
        DetectedMimeType = file.MetadataState == "succeeded" ? file.MimeType : null,
        MediaFormat = file.MediaFormat,
        Width = file.Width,
        Height = file.Height,
        Orientation = file.Orientation,
        EmbeddedCapturedAt = file.EmbeddedCapturedAt,
        FilenameCapturedAt = file.FilenameCapturedAt,
        ImportedCapturedAt = includeImportedDetails ? file.ImportedCapturedAt : null,
        Description = includeImportedDetails ? file.Description : null,
        Latitude = includeImportedDetails ? file.Latitude : null,
        Longitude = includeImportedDetails ? file.Longitude : null,
        AltitudeMeters = includeImportedDetails ? file.AltitudeMeters : null,
        ProcessedAt = file.MetadataProcessedAt,
        RecoveryManifestState = file.RecoveryManifestState
    };

    private async Task<Dictionary<Guid, IReadOnlyList<TagSummaryRes>>> TagsForItemsAsync(
        Guid[] fileIds,
        Guid[] folderIds,
        CancellationToken cancellationToken)
    {
        var rows = new List<TagItemRow>();
        if (fileIds.Length > 0)
        {
            rows.AddRange(await (
                from assignment in dbContext.FileTags.AsNoTracking()
                join tag in dbContext.Tags.AsNoTracking() on assignment.TagId equals tag.Id
                where fileIds.Contains(assignment.FileId)
                orderby tag.Name
                select new TagItemRow(assignment.FileId, tag.Id, tag.Name, tag.Color))
                .ToListAsync(cancellationToken));
        }
        if (folderIds.Length > 0)
        {
            rows.AddRange(await (
                from assignment in dbContext.FolderTags.AsNoTracking()
                join tag in dbContext.Tags.AsNoTracking() on assignment.TagId equals tag.Id
                where folderIds.Contains(assignment.FolderId)
                orderby tag.Name
                select new TagItemRow(assignment.FolderId, tag.Id, tag.Name, tag.Color))
                .ToListAsync(cancellationToken));
        }
        return rows.GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<TagSummaryRes>)group
                .Select(row => new TagSummaryRes { Id = row.TagId, Name = row.Name, Color = row.Color }).ToList());
    }

    private static string? NormalizeItemSort(string? sort) => (sort ?? "name_asc").Trim().ToLowerInvariant() switch
    {
        "name_asc" or "name_desc" or "modified_desc" or "modified_asc" or "size_desc" or "size_asc" => (sort ?? "name_asc").Trim().ToLowerInvariant(),
        _ => null
    };

    private static string CopyName(string sourceName, IReadOnlyCollection<string> siblingNames)
    {
        var existing = siblingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extension = Path.GetExtension(sourceName);
        var stem = Path.GetFileNameWithoutExtension(sourceName);
        for (var copyNumber = 1; ; copyNumber++)
        {
            var suffix = copyNumber == 1 ? string.Empty : $" ({copyNumber})";
            var prefix = $"Copy of {stem}";
            var maxExtensionLength = Math.Max(0, 254 - suffix.Length);
            var safeExtension = extension.Length > maxExtensionLength
                ? extension[..maxExtensionLength]
                : extension;
            var maxPrefixLength = Math.Max(1, 255 - safeExtension.Length - suffix.Length);
            if (prefix.Length > maxPrefixLength) prefix = prefix[..maxPrefixLength];
            var candidate = $"{prefix}{suffix}{safeExtension}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private static FileVersionMetadata CopyVersionMetadata(
        FileVersionMetadata? source,
        Guid copyVersionId,
        CatalogFile sourceFile)
    {
        if (source is null)
        {
            return new FileVersionMetadata
            {
                FileVersionId = copyVersionId,
                State = sourceFile.MetadataState == "processing" ? "pending" : sourceFile.MetadataState,
                AttemptCount = sourceFile.MetadataAttemptCount,
                ProcessedAt = sourceFile.MetadataProcessedAt,
                ErrorCode = sourceFile.MetadataErrorCode,
                ErrorMessage = sourceFile.MetadataErrorMessage,
                DetectedMimeType = sourceFile.MimeType,
                MediaFormat = sourceFile.MediaFormat,
                Width = sourceFile.Width,
                Height = sourceFile.Height,
                Orientation = sourceFile.Orientation,
                EmbeddedCreatedAt = sourceFile.EmbeddedCapturedAt,
                FilenameCapturedAt = sourceFile.FilenameCapturedAt,
                ImportedCapturedAt = sourceFile.ImportedCapturedAt,
                ImportedDescription = sourceFile.Description,
                ImportedLatitude = sourceFile.Latitude,
                ImportedLongitude = sourceFile.Longitude,
                ImportedAltitudeMeters = sourceFile.AltitudeMeters,
                EffectiveCapturedAt = sourceFile.CapturedAt,
                CaptureDateSource = sourceFile.CaptureDateSource ?? "uploaded_at",
                RecoveryManifestState = sourceFile.RecoveryManifestState
            };
        }

        return new FileVersionMetadata
        {
            FileVersionId = copyVersionId,
            State = source.State == "processing" ? "pending" : source.State,
            AttemptCount = source.AttemptCount,
            ProcessedAt = source.ProcessedAt,
            ExtractorVersion = source.ExtractorVersion,
            ErrorCode = source.ErrorCode,
            ErrorMessage = source.ErrorMessage,
            DetectedMimeType = source.DetectedMimeType,
            MediaFormat = source.MediaFormat,
            Width = source.Width,
            Height = source.Height,
            Orientation = source.Orientation,
            ExifDateTimeOriginal = source.ExifDateTimeOriginal,
            ExifDateTimeDigitized = source.ExifDateTimeDigitized,
            EmbeddedCreatedAt = source.EmbeddedCreatedAt,
            FilenameCapturedAt = source.FilenameCapturedAt,
            ImportedCapturedAt = source.ImportedCapturedAt,
            ImportedDescription = source.ImportedDescription,
            ImportedLatitude = source.ImportedLatitude,
            ImportedLongitude = source.ImportedLongitude,
            ImportedAltitudeMeters = source.ImportedAltitudeMeters,
            EffectiveCapturedAt = source.EffectiveCapturedAt,
            CaptureDateSource = source.CaptureDateSource,
            RawMetadataJson = source.RawMetadataJson,
            RecoveryManifestState = source.RecoveryManifestState,
            RecoveryManifestError = source.RecoveryManifestError,
            RecoveryManifestWrittenAt = source.RecoveryManifestWrittenAt
        };
    }

    private static string? NormalizeFileType(string? type) => (type ?? "all").Trim().ToLowerInvariant() switch
    {
        "all" or "folder" or "document" or "pdf" or "spreadsheet" or "image" or "video" => (type ?? "all").Trim().ToLowerInvariant(),
        _ => null
    };

    private static IQueryable<CatalogFile> ApplyFileTypeFilter(IQueryable<CatalogFile> query, string type) => type switch
    {
        "folder" => query.Where(_ => false),
        "document" => query.Where(file => file.MimeType.StartsWith("text/")
            || file.MimeType == "application/msword"
            || file.MimeType == "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            || file.MimeType == "application/vnd.oasis.opendocument.text"
            || file.MimeType == "application/rtf"),
        // Metadata normalizes MIME types asynchronously. Keep PDFs discoverable while
        // that work is pending or for older catalog rows that retained a generic MIME
        // type, using the user-visible filename only as a narrow fallback.
        "pdf" => query.Where(file => file.MimeType == "application/pdf"
            || EF.Functions.Like(file.Name, "%.pdf")),
        "spreadsheet" => query.Where(file => file.MimeType == "text/csv"
            || file.MimeType == "application/vnd.ms-excel"
            || file.MimeType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            || file.MimeType == "application/vnd.oasis.opendocument.spreadsheet"),
        "image" => query.Where(file => file.MimeType.StartsWith("image/")),
        "video" => query.Where(file => file.MimeType.StartsWith("video/")),
        _ => query
    };

    private static string? NormalizePhotoSort(string? sort) => (sort ?? "captured_desc").Trim().ToLowerInvariant() switch
    {
        "captured_desc" or "captured_asc" or "added_desc" => (sort ?? "captured_desc").Trim().ToLowerInvariant(),
        _ => null
    };

    private static IQueryable<BrowseEntry> ApplyItemSort(IQueryable<BrowseEntry> query, string sort) => sort switch
    {
        "name_desc" => query.OrderByDescending(entry => entry.IsFolder).ThenByDescending(entry => entry.Name).ThenByDescending(entry => entry.Sequence),
        "modified_desc" => query.OrderByDescending(entry => entry.IsFolder).ThenByDescending(entry => entry.ModifiedAt).ThenByDescending(entry => entry.Sequence),
        "modified_asc" => query.OrderByDescending(entry => entry.IsFolder).ThenBy(entry => entry.ModifiedAt).ThenBy(entry => entry.Sequence),
        "size_desc" => query.OrderByDescending(entry => entry.IsFolder).ThenByDescending(entry => entry.SizeBytes).ThenBy(entry => entry.Name).ThenBy(entry => entry.Sequence),
        "size_asc" => query.OrderByDescending(entry => entry.IsFolder).ThenBy(entry => entry.SizeBytes).ThenBy(entry => entry.Name).ThenBy(entry => entry.Sequence),
        _ => query.OrderByDescending(entry => entry.IsFolder).ThenBy(entry => entry.Name).ThenBy(entry => entry.Sequence)
    };

    private static IQueryable<CatalogFile> ApplyPhotoSort(IQueryable<CatalogFile> query, string sort) => sort switch
    {
        "captured_asc" => query.OrderBy(file => file.CapturedAt).ThenBy(file => file.Sequence),
        "added_desc" => query.OrderByDescending(file => file.CreatedAt).ThenByDescending(file => file.Sequence),
        _ => query.OrderByDescending(file => file.CapturedAt).ThenByDescending(file => file.Sequence)
    };

    private static IQueryable<CatalogFile> ApplyReversePhotoSort(IQueryable<CatalogFile> query, string sort) => sort switch
    {
        "captured_asc" => query.OrderByDescending(file => file.CapturedAt).ThenByDescending(file => file.Sequence),
        "added_desc" => query.OrderBy(file => file.CreatedAt).ThenBy(file => file.Sequence),
        _ => query.OrderBy(file => file.CapturedAt).ThenBy(file => file.Sequence)
    };

    private async Task<Result<PhotoQuery>> BuildPhotoQueryAsync(
        Guid userId,
        string scope,
        Guid? folderId,
        string type,
        bool favorite,
        int? year,
        string? captureDate,
        Guid? tagId,
        CancellationToken cancellationToken)
    {
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "folder" : scope.Trim().ToLowerInvariant();
        var normalizedType = string.IsNullOrWhiteSpace(type) ? "all" : type.Trim().ToLowerInvariant();
        if (normalizedScope is not ("all" or "folder")) return MediaErrors.Validation("Photo scope must be all or folder.");
        if (normalizedType is not ("all" or "image" or "video")) return MediaErrors.Validation("Photo type must be all, image, or video.");
        if (year is < 1 or > 9999) return MediaErrors.Validation("Photo year must be between 1 and 9999.");
        if (!TryParseCaptureDateFilter(captureDate, out var captureRange)) return MediaErrors.Validation("Photo capture date must be YYYY, YYYY-MM, or YYYY-MM-DD.");

        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        if (!await TagBelongsToLibraryAsync(tagId, library.Id, cancellationToken))
            return MediaErrors.NotFound("The requested tag was not found in your library.");

        var query = dbContext.Files.AsNoTracking().Where(file => file.LibraryId == library.Id
            && file.TrashedAt == null
            && (file.MimeType.StartsWith("image/") || file.MimeType.StartsWith("video/")));
        if (normalizedScope == "folder")
        {
            var selectedFolderId = folderId ?? await dbContext.Folders.AsNoTracking()
                .Where(folder => folder.LibraryId == library.Id && folder.SystemRole == LibraryBootstrapper.MediaFolderRole)
                .Select(folder => folder.Id)
                .SingleAsync(cancellationToken);
            var folderIds = await DescendantFolderIdsAsync(library.Id, selectedFolderId, cancellationToken);
            query = query.Where(file => file.ParentFolderId.HasValue && folderIds.Contains(file.ParentFolderId.Value));
        }

        if (normalizedType == "image") query = query.Where(file => file.MimeType.StartsWith("image/"));
        if (normalizedType == "video") query = query.Where(file => file.MimeType.StartsWith("video/"));
        if (favorite) query = query.Where(file => dbContext.FileUserStates.Any(state => state.UserId == userId && state.FileId == file.Id && state.Favorite));
        if (tagId.HasValue) query = query.Where(file => dbContext.FileTags.Any(item => item.FileId == file.Id && item.TagId == tagId));
        if (year.HasValue)
        {
            var yearStart = new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            query = query.Where(file => file.CapturedAt >= yearStart && file.CapturedAt < yearStart.AddYears(1));
        }
        if (captureRange is not null)
            query = query.Where(file => file.CapturedAt >= captureRange.Start && file.CapturedAt < captureRange.End);
        return new PhotoQuery(query, library.Id, normalizedScope, normalizedType);
    }

    private static IQueryable<CatalogFile> ApplyPhotoCursor(IQueryable<CatalogFile> query, PhotoCursor? cursor, string sort)
    {
        if (cursor is null) return query;
        if (cursor.Kind == "anchor" && cursor.Year.HasValue && cursor.Month.HasValue)
        {
            if (cursor.Day.HasValue)
            {
                var dayStart = new DateTime(cursor.Year.Value, cursor.Month.Value, cursor.Day.Value, 0, 0, 0, DateTimeKind.Utc);
                return sort == "captured_asc"
                    ? query.Where(file => file.CapturedAt >= dayStart)
                    : query.Where(file => file.CapturedAt < dayStart.AddDays(1));
            }
            var monthStart = new DateTime(cursor.Year.Value, cursor.Month.Value, 1, 0, 0, 0, DateTimeKind.Utc);
            return sort == "captured_asc"
                ? query.Where(file => file.CapturedAt >= monthStart)
                : query.Where(file => file.CapturedAt < monthStart.AddMonths(1));
        }

        if (cursor.Kind != "item" || !cursor.Ticks.HasValue || !cursor.Sequence.HasValue) return query;
        var position = new DateTime(cursor.Ticks.Value, DateTimeKind.Utc);
        var previous = cursor.Direction == "previous";
        return sort switch
        {
            "captured_asc" when previous => query.Where(file => file.CapturedAt < position
                || (file.CapturedAt == position && file.Sequence < cursor.Sequence.Value)),
            "captured_asc" => query.Where(file => file.CapturedAt > position
                || (file.CapturedAt == position && file.Sequence > cursor.Sequence.Value)),
            "added_desc" when previous => query.Where(file => file.CreatedAt > position
                || (file.CreatedAt == position && file.Sequence > cursor.Sequence.Value)),
            "added_desc" => query.Where(file => file.CreatedAt < position
                || (file.CreatedAt == position && file.Sequence < cursor.Sequence.Value)),
            _ when previous => query.Where(file => file.CapturedAt > position
                || (file.CapturedAt == position && file.Sequence > cursor.Sequence.Value)),
            _ => query.Where(file => file.CapturedAt < position
                || (file.CapturedAt == position && file.Sequence < cursor.Sequence.Value))
        };
    }

    private static PhotoCursor? DecodePhotoCursor(string? cursor, string expectedSort)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<PhotoCursor>(Convert.FromBase64String(cursor));
            if (parsed?.Version != 1 || parsed.Sort != expectedSort) return null;
            if (parsed.Kind == "anchor")
                return parsed.Year is >= 1 and <= 9999
                    && parsed.Month is >= 1 and <= 12
                    && (!parsed.Day.HasValue || DateOnly.TryParseExact($"{parsed.Year:D4}-{parsed.Month:D2}-{parsed.Day:D2}", "yyyy-MM-dd", out _))
                        ? parsed
                        : null;
            return parsed.Kind == "item"
                && parsed.Ticks.HasValue
                && parsed.Sequence.HasValue
                && parsed.Direction is "next" or "previous"
                    ? parsed
                    : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private static PhotoCursor PhotoItemCursor(string sort, CatalogFile file, string direction) =>
        new()
        {
            Version = 1,
            Kind = "item",
            Sort = sort,
            Direction = direction,
            Ticks = (sort == "added_desc" ? file.CreatedAt : file.CapturedAt).Ticks,
            Sequence = file.Sequence
        };

    private static string EncodePhotoItemCursor(string sort, CatalogFile file, string direction) =>
        EncodePhotoCursor(PhotoItemCursor(sort, file, direction));

    private static string EncodePhotoAnchorCursor(string sort, int year, int month) =>
        EncodePhotoCursor(new PhotoCursor { Version = 1, Kind = "anchor", Sort = sort, Year = year, Month = month });

    private static string EncodePhotoCursor(PhotoCursor cursor) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor));

    private static int DecodeOffsetCursor(string? cursor, string expectedSort)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':', 2);
            return parts.Length == 2 && parts[0] == expectedSort && int.TryParse(parts[1], out var offset) && offset >= 0
                ? offset
                : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static string EncodeOffsetCursor(string sort, int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sort}:{offset}"));

    private sealed record PhotoQuery(
        IQueryable<CatalogFile> Query,
        Guid LibraryId,
        string NormalizedScope,
        string NormalizedType);

    private sealed class PhotoCursor
    {
        public int Version { get; init; }
        public string Kind { get; init; } = null!;
        public string Sort { get; init; } = null!;
        public string Direction { get; init; } = "next";
        public long? Ticks { get; init; }
        public long? Sequence { get; init; }
        public int? Year { get; init; }
        public int? Month { get; init; }
        public int? Day { get; init; }
    }

    private async Task<List<SearchCandidate>> QueryFileCandidatesAsync(
        Guid libraryId,
        Guid userId,
        string query,
        Guid rootFolderId,
        bool recursive,
        bool includePhotos,
        bool favorite,
        string type,
        Guid? tagId,
        string sort,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE location_folders(Id) AS (
                  SELECT Id
                  FROM folders
                  WHERE Id = $rootFolderId COLLATE NOCASE
                    AND LibraryId = $libraryId COLLATE NOCASE
                    AND TrashedAt IS NULL
                  UNION ALL
                  SELECT child.Id
                  FROM folders child
                  JOIN location_folders parent ON child.ParentFolderId = parent.Id
                  WHERE child.LibraryId = $libraryId COLLATE NOCASE
                    AND child.TrashedAt IS NULL
                    AND $recursive = 1
                ),
                media_folders(Id) AS (
                  SELECT Id
                  FROM folders
                  WHERE LibraryId = $libraryId COLLATE NOCASE
                    AND SystemRole = 'media_root'
                    AND TrashedAt IS NULL
                  UNION ALL
                  SELECT child.Id
                  FROM folders child
                  JOIN media_folders parent ON child.ParentFolderId = parent.Id
                  WHERE child.LibraryId = $libraryId COLLATE NOCASE
                    AND child.TrashedAt IS NULL
                )
                SELECT catalog_search.item_id, catalog_search.kind
                FROM catalog_search
                LEFT JOIN folders sort_folder
                  ON catalog_search.kind = 'folder'
                    AND sort_folder.Id = catalog_search.item_id COLLATE NOCASE
                LEFT JOIN files sort_file
                  ON catalog_search.kind = 'file'
                    AND sort_file.Id = catalog_search.item_id COLLATE NOCASE
                WHERE catalog_search MATCH $query
                  AND library_id = $libraryId COLLATE NOCASE
                  AND (
                    (kind = 'folder'
                      AND $type IN ('all', 'folder')
                      AND EXISTS (
                        SELECT 1 FROM folders folder
                        WHERE folder.Id = catalog_search.item_id COLLATE NOCASE
                          AND folder.LibraryId = $libraryId COLLATE NOCASE
                          AND folder.TrashedAt IS NULL
                          AND folder.ParentFolderId IS NOT NULL
                          AND (
                            folder.ParentFolderId IN (SELECT Id FROM location_folders)
                            OR ($includePhotos = 1 AND folder.ParentFolderId IN (SELECT Id FROM media_folders))
                          )
                          AND ($tagId IS NULL OR EXISTS (
                            SELECT 1 FROM folder_tags assignment
                            WHERE assignment.FolderId = folder.Id
                              AND assignment.TagId = $tagId COLLATE NOCASE
                          ))
                          AND ($favorite = 0 OR EXISTS (
                            SELECT 1 FROM folder_user_states state
                            WHERE state.UserId = $userId COLLATE NOCASE
                              AND state.FolderId = folder.Id
                              AND state.Favorite = 1
                          ))
                      )
                    )
                    OR
                    (kind = 'file'
                      AND $type <> 'folder'
                      AND EXISTS (
                        SELECT 1 FROM files file
                        WHERE file.Id = catalog_search.item_id COLLATE NOCASE
                          AND file.LibraryId = $libraryId COLLATE NOCASE
                          AND file.TrashedAt IS NULL
                          AND file.ParentFolderId IS NOT NULL
                          AND (
                            file.ParentFolderId IN (SELECT Id FROM location_folders)
                            OR ($includePhotos = 1 AND file.ParentFolderId IN (SELECT Id FROM media_folders))
                          )
                          AND ($tagId IS NULL OR EXISTS (
                            SELECT 1 FROM file_tags assignment
                            WHERE assignment.FileId = file.Id
                              AND assignment.TagId = $tagId COLLATE NOCASE
                          ))
                          AND ($favorite = 0 OR EXISTS (
                            SELECT 1 FROM file_user_states state
                            WHERE state.UserId = $userId COLLATE NOCASE
                              AND state.FileId = file.Id
                              AND state.Favorite = 1
                          ))
                          AND (
                            $type = 'all'
                            OR ($type = 'document' AND (
                              file.MimeType LIKE 'text/%'
                              OR file.MimeType IN ('application/msword', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'application/vnd.oasis.opendocument.text', 'application/rtf')
                            ))
                            OR ($type = 'pdf' AND (
                              file.MimeType = 'application/pdf'
                              OR lower(file.Name) LIKE '%.pdf'
                            ))
                            OR ($type = 'spreadsheet' AND (
                              file.MimeType IN ('text/csv', 'application/vnd.ms-excel', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', 'application/vnd.oasis.opendocument.spreadsheet')
                            ))
                            OR ($type = 'image' AND file.MimeType LIKE 'image/%')
                            OR ($type = 'video' AND file.MimeType LIKE 'video/%')
                          )
                      )
                    )
                  )
                ORDER BY
                  CASE WHEN catalog_search.kind = 'folder' THEN 1 ELSE 0 END DESC,
                  CASE WHEN $sort = 'name_asc' THEN COALESCE(sort_folder.Name, sort_file.Name) END COLLATE NOCASE ASC,
                  CASE WHEN $sort = 'name_desc' THEN COALESCE(sort_folder.Name, sort_file.Name) END COLLATE NOCASE DESC,
                  CASE WHEN $sort = 'modified_desc' THEN COALESCE(sort_folder.ModifiedAt, sort_file.ModifiedAt) END DESC,
                  CASE WHEN $sort = 'modified_asc' THEN COALESCE(sort_folder.ModifiedAt, sort_file.ModifiedAt) END ASC,
                  CASE WHEN $sort = 'size_desc' THEN COALESCE(sort_file.SizeBytes, 0) END DESC,
                  CASE WHEN $sort = 'size_asc' THEN COALESCE(sort_file.SizeBytes, 0) END ASC,
                  CASE WHEN $sort IN ('size_desc', 'size_asc') THEN COALESCE(sort_folder.Name, sort_file.Name) END COLLATE NOCASE ASC,
                  CASE WHEN $sort IN ('name_desc', 'modified_desc') THEN COALESCE(sort_folder.Sequence, sort_file.Sequence) END DESC,
                  CASE WHEN $sort IN ('name_asc', 'modified_asc', 'size_desc', 'size_asc') THEN COALESCE(sort_folder.Sequence, sort_file.Sequence) END ASC,
                  catalog_search.item_id
                LIMIT $limit OFFSET $offset;
                """;
            AddCommandParameter(command, "$query", query);
            AddCommandParameter(command, "$libraryId", libraryId.ToString("D"));
            AddCommandParameter(command, "$userId", userId.ToString("D"));
            AddCommandParameter(command, "$rootFolderId", rootFolderId.ToString("D"));
            AddCommandParameter(command, "$recursive", recursive ? 1 : 0);
            AddCommandParameter(command, "$includePhotos", includePhotos ? 1 : 0);
            AddCommandParameter(command, "$favorite", favorite ? 1 : 0);
            AddCommandParameter(command, "$type", type);
            AddCommandParameter(command, "$tagId", tagId.HasValue ? tagId.Value.ToString("D") : DBNull.Value);
            AddCommandParameter(command, "$sort", sort);
            AddCommandParameter(command, "$limit", limit + 1);
            AddCommandParameter(command, "$offset", offset);

            var candidates = new List<SearchCandidate>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                    candidates.Add(new SearchCandidate(id, reader.GetString(1)));
            }
            return candidates;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private static void AddCommandParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task<List<SearchCandidate>> SearchCandidatesAsync(Guid libraryId, string query, string scope, string photoScope, string type, Guid? tagId, int offset, int limit, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE media_folders(Id) AS (
                  SELECT Id
                  FROM folders
                  WHERE LibraryId = $libraryId COLLATE NOCASE
                    AND SystemRole = 'media_root'
                  UNION ALL
                  SELECT child.Id
                  FROM folders child
                  JOIN media_folders parent ON child.ParentFolderId = parent.Id
                  WHERE child.LibraryId = $libraryId COLLATE NOCASE
                )
                SELECT item_id, kind
                FROM catalog_search
                WHERE catalog_search MATCH $query
                  AND library_id = $libraryId COLLATE NOCASE
                  AND (
                    (kind = 'folder'
                      AND $scope = 'trash'
                      AND $type IN ('all', 'folder')
                      AND EXISTS (
                        SELECT 1 FROM folders folder
                        WHERE folder.Id = catalog_search.item_id COLLATE NOCASE
                          AND folder.LibraryId = $libraryId COLLATE NOCASE
                          AND folder.TrashedAt IS NOT NULL
                          AND ($tagId IS NULL OR EXISTS (
                            SELECT 1 FROM folder_tags assignment
                            WHERE assignment.FolderId = folder.Id
                              AND assignment.TagId = $tagId COLLATE NOCASE
                          ))
                        )
                    )
                    OR
                    (kind = 'file'
                      AND $type <> 'folder'
                      AND EXISTS (
                        SELECT 1 FROM files file
                        WHERE file.Id = catalog_search.item_id COLLATE NOCASE
                          AND file.LibraryId = $libraryId COLLATE NOCASE
                          AND ($tagId IS NULL OR EXISTS (
                            SELECT 1 FROM file_tags assignment
                            WHERE assignment.FileId = file.Id
                              AND assignment.TagId = $tagId COLLATE NOCASE
                          ))
                          AND (
                            ($scope = 'trash' AND file.TrashedAt IS NOT NULL)
                            OR
                            ($scope = 'photos'
                              AND file.TrashedAt IS NULL
                              AND (file.MimeType LIKE 'image/%' OR file.MimeType LIKE 'video/%')
                              AND ($photoScope = 'all' OR file.ParentFolderId IN (SELECT Id FROM media_folders)))
                          )
                          AND (
                            $type = 'all'
                            OR ($type = 'document' AND (
                              file.MimeType LIKE 'text/%'
                              OR file.MimeType IN ('application/msword', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'application/vnd.oasis.opendocument.text', 'application/rtf')
                            ))
                            OR ($type = 'pdf' AND (
                              file.MimeType = 'application/pdf'
                              OR lower(file.Name) LIKE '%.pdf'
                            ))
                            OR ($type = 'spreadsheet' AND (
                              file.MimeType IN ('text/csv', 'application/vnd.ms-excel', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', 'application/vnd.oasis.opendocument.spreadsheet')
                            ))
                            OR ($type = 'image' AND file.MimeType LIKE 'image/%')
                            OR ($type = 'video' AND file.MimeType LIKE 'video/%')
                          )
                      )
                    )
                  )
                ORDER BY rank, item_id
                LIMIT $limit OFFSET $offset;
                """;
            AddCommandParameter(command, "$query", query);
            AddCommandParameter(command, "$libraryId", libraryId.ToString("D"));
            AddCommandParameter(command, "$scope", scope);
            AddCommandParameter(command, "$photoScope", photoScope);
            AddCommandParameter(command, "$type", type);
            AddCommandParameter(command, "$tagId", tagId.HasValue ? tagId.Value.ToString("D") : DBNull.Value);
            AddCommandParameter(command, "$limit", limit + 1);
            AddCommandParameter(command, "$offset", offset);

            var candidates = new List<SearchCandidate>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                {
                    candidates.Add(new SearchCandidate(id, reader.GetString(1)));
                }
            }
            return candidates;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private Task<bool> TagBelongsToLibraryAsync(Guid? tagId, Guid libraryId, CancellationToken cancellationToken) =>
        !tagId.HasValue
            ? Task.FromResult(true)
            : dbContext.Tags.AsNoTracking().AnyAsync(
                tag => tag.Id == tagId.Value && tag.LibraryId == libraryId,
                cancellationToken);

    private async Task<Dictionary<Guid, string>> CatalogPathsAsync(Guid libraryId, bool includeTrashed, CancellationToken cancellationToken)
    {
        var folders = await dbContext.Folders.AsNoTracking()
            .Where(folder => folder.LibraryId == libraryId && (includeTrashed || folder.TrashedAt == null))
            .Select(folder => new FolderPathEntry(folder.Id, folder.ParentFolderId, folder.Name))
            .ToListAsync(cancellationToken);
        var byId = folders.ToDictionary(folder => folder.Id);
        var paths = new Dictionary<Guid, string>();
        foreach (var folder in folders)
        {
            BuildPath(folder.Id, new HashSet<Guid>());
        }
        return paths;

        string BuildPath(Guid folderId, HashSet<Guid> ancestors)
        {
            if (paths.TryGetValue(folderId, out var existing)) return existing;
            if (!byId.TryGetValue(folderId, out var folder) || !ancestors.Add(folderId)) return string.Empty;
            var parentPath = folder.ParentFolderId.HasValue
                ? BuildPath(folder.ParentFolderId.Value, ancestors)
                : string.Empty;
            ancestors.Remove(folderId);
            var path = string.IsNullOrEmpty(parentPath) ? folder.Name : $"{parentPath} / {folder.Name}";
            paths[folderId] = path;
            return path;
        }
    }

    private static string? ToFtsQuery(string? query)
    {
        var terms = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Any(char.IsLetterOrDigit))
            .ToArray();
        if (terms.Sum(term => term.Count(char.IsLetterOrDigit)) < 2) return null;
        return string.Join(" AND ", terms.Select(term => "\"" + term.Replace("\"", "\"\"") + "\"*"));
    }

    private static int DecodeSearchCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(value, out var offset) && offset >= 0 ? offset : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static string EncodeSearchCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));

    private IQueryable<Blob> TrashDeletableBlobs(Guid libraryId) =>
        dbContext.Blobs.Where(blob =>
            blob.LifecycleState == Blob.AvailableLifecycleState
                && dbContext.FileVersions.Any(version =>
                    version.BlobSha256 == blob.Sha256
                    && dbContext.Files.Any(file =>
                        file.Id == version.FileId
                        && file.LibraryId == libraryId
                        && file.TrashedAt != null))
                && !dbContext.FileVersions.Any(version =>
                    version.BlobSha256 == blob.Sha256
                    && !dbContext.Files.Any(file =>
                        file.Id == version.FileId
                        && file.LibraryId == libraryId
                        && file.TrashedAt != null)));

    private static string StorageSummaryCacheKey(Guid userId) => $"{StorageSummaryCacheKeyPrefix}{userId:N}";

    internal void InvalidateStorageSummary(Guid userId) => storageSummaryCache.Remove(StorageSummaryCacheKey(userId));

    private static bool TryParseCaptureDateFilter(string? value, out CaptureDateRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var normalized = value.Trim();
        if (normalized.Length == 4 && int.TryParse(normalized, out var year) && year is >= 1 and <= 9999)
        {
            var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            range = new CaptureDateRange(start, start.AddYears(1));
            return true;
        }
        if (DateTime.TryParseExact(normalized, "yyyy-MM", null, System.Globalization.DateTimeStyles.None, out var month))
        {
            var start = DateTime.SpecifyKind(month, DateTimeKind.Utc);
            range = new CaptureDateRange(start, start.AddMonths(1));
            return true;
        }
        if (DateTime.TryParseExact(normalized, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day))
        {
            var start = DateTime.SpecifyKind(day, DateTimeKind.Utc);
            range = new CaptureDateRange(start, start.AddDays(1));
            return true;
        }
        return false;
    }

    private static bool TryParseTimelineSeekDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", out var parsed)) return false;
        date = parsed;
        return true;
    }

    private static string KindFor(string mimeType)
    {
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "image";
        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "video";
        return "document";
    }

    private static bool IsMedia(string mimeType) => mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static string? ThumbnailPathFor(Guid id, string mimeType) => IsMedia(mimeType)
        ? $"/api/v1/items/{id}/thumbnail?size=md"
        : null;

    private static Result<int> ThumbnailPixels(string size) => size.ToLowerInvariant() switch
    {
        "sm" => 160,
        "md" => 320,
        "lg" => 1024,
        _ => MediaErrors.Validation("Thumbnail size must be sm, md, or lg.")
    };

    private sealed record FileBlob(
        Guid Id,
        string Name,
        string MimeType,
        long SizeBytes,
        string BlobSha256,
        DateTime VersionCreatedAt);

    private sealed record ArchiveFolder(Guid Id, Guid? ParentFolderId, string Name, long Sequence);

    private sealed record ArchiveCatalogFile(Guid Id, Guid ParentFolderId, string Name, long Sequence, long SizeBytes);

    private sealed record ArchiveEntryFile(Guid Id, string EntryName, long SizeBytes);

    private sealed record ArchivePlan(IReadOnlyList<string> Directories, IReadOnlyList<ArchiveEntryFile> Files);

    private sealed record FolderArchiveSource(Guid Id, string Name, HashSet<Guid> FolderIds, ArchivePlan Plan);

    private sealed record SelectedDownloadFile(int Index, FileBlob File);

    private sealed record SelectedDownloadFolder(int Index, FolderArchiveSource Source);

    private sealed record SelectedArchiveItem(int Index, FolderArchiveSource? Folder, FileBlob? File);

    private sealed record SearchCandidate(Guid Id, string Kind);

    private sealed record TagItemRow(Guid ItemId, Guid TagId, string Name, string Color);

    private sealed record FolderPathEntry(Guid Id, Guid? ParentFolderId, string Name);

    private sealed record CaptureDateRange(DateTime Start, DateTime End);

    /// <summary>
    /// Preserves ZIP byte ordering while translating the small synchronous headers
    /// emitted by ZipArchive into asynchronous writes to the HTTP response stream.
    /// </summary>
    private sealed class AsyncZipWriteStream(Stream destination) : Stream
    {
        private readonly MemoryStream pendingSynchronousWrites = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            // ZipArchive can request a synchronous flush. CompleteAsync performs
            // the corresponding asynchronous flush before the response finishes.
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            FlushAsyncCore(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            pendingSynchronousWrites.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) =>
            pendingSynchronousWrites.Write(buffer);

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WriteAsyncCore(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            WriteAsyncCore(buffer, cancellationToken);

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            await FlushPendingAsync(cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        private async Task FlushAsyncCore(CancellationToken cancellationToken)
        {
            await FlushPendingAsync(cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        private async ValueTask WriteAsyncCore(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            await FlushPendingAsync(cancellationToken);
            await destination.WriteAsync(buffer, cancellationToken);
        }

        private async ValueTask FlushPendingAsync(CancellationToken cancellationToken)
        {
            if (pendingSynchronousWrites.Length == 0) return;
            var pending = pendingSynchronousWrites.GetBuffer()
                .AsMemory(0, checked((int)pendingSynchronousWrites.Length));
            await destination.WriteAsync(pending, cancellationToken);
            pendingSynchronousWrites.SetLength(0);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) pendingSynchronousWrites.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CompositeDisposable(params IDisposable[] resources) : IDisposable
    {
        public void Dispose()
        {
            foreach (var resource in resources) resource.Dispose();
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class BrowseEntry
    {
        public Guid Id { get; init; }
        public bool IsFolder { get; init; }
        public string Name { get; init; } = null!;
        public DateTime ModifiedAt { get; init; }
        public long? SizeBytes { get; init; }
        public long Sequence { get; init; }
    }
}
