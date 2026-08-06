using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface IMediaDeliveryService
{
    Task<Result<DownloadPreviewRes>> PreviewDownloadAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);
    Task<Result<DownloadPreviewRes>> PreviewFolderDownloadAsync(Guid userId, Guid folderId, CancellationToken cancellationToken);
    Task<Result<DownloadPreviewRes>> PreviewAlbumDownloadAsync(Guid userId, Guid albumId, CancellationToken cancellationToken);
    Task<Result<MediaDownloadContent>> PrepareDownloadAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);
    Task<Result<MediaDownloadContent>> PrepareFolderDownloadAsync(Guid userId, Guid folderId, CancellationToken cancellationToken);
    Task<Result<MediaDownloadContent>> PrepareAlbumDownloadAsync(Guid userId, Guid albumId, CancellationToken cancellationToken);
    Task<Result<MediaBinaryInfo>> GetContentInfoAsync(Guid userId, Guid itemId, CancellationToken cancellationToken);
    Task<Result<MediaBinaryContent>> OpenContentAsync(Guid userId, Guid itemId, long? rangeStart, long? rangeEnd, CancellationToken cancellationToken);
    Task<Result<MediaBinaryContent>> OpenThumbnailAsync(Guid userId, Guid itemId, string size, CancellationToken cancellationToken);
}
