using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface ICatalogQueryService
{
    Task<Result<BrowseItemsRes>> QueryFilesAsync(Guid userId, FileQueryOptions options, CancellationToken cancellationToken);
    Task<Result<BrowseItemsRes>> BrowseTrashAsync(Guid userId, string? cursor, int limit, string type, string sort, CancellationToken cancellationToken);
    Task<Result<BrowseItemsRes>> BrowseFavoriteFoldersAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken);
    Task<Result<BrowseItemsRes>> BrowsePhotosAsync(Guid userId, string scope, Guid? folderId, string? cursor, int limit, string type, bool favorite, string sort, int? year, string? captureDate, string? seekDate, Guid? tagId, CancellationToken cancellationToken);
    Task<Result<PhotoTimelineYearsRes>> BrowsePhotoYearsAsync(Guid userId, string scope, Guid? folderId, string type, bool favorite, Guid? tagId, CancellationToken cancellationToken);
    Task<Result<PhotoTimelineAnchorsRes>> BrowsePhotoTimelineAnchorsAsync(Guid userId, string scope, Guid? folderId, string type, bool favorite, string sort, int? year, string? captureDate, Guid? tagId, CancellationToken cancellationToken);
    Task<Result<BrowseItemsRes>> SearchAsync(Guid userId, string query, string scope, string photoScope, string type, string? cursor, int limit, Guid? tagId, CancellationToken cancellationToken);
    Task<Result<TrashSummaryRes>> GetTrashSummaryAsync(Guid userId, CancellationToken cancellationToken);
    Task<Result<TrashPreviewRes>> PreviewTrashAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);
    Task<Result<StorageSummaryRes>> GetStorageSummaryAsync(Guid userId, bool refresh, CancellationToken cancellationToken);
    Task<Result<MediaItemRes>> GetItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken);
}
