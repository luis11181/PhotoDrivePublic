using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface ICatalogCommandService
{
    Task<Result<MediaItemRes>> CreateFolderAsync(Guid userId, CreateFolderReq request, CancellationToken cancellationToken);
    Task<Result<MediaItemRes>> RenameItemAsync(Guid userId, Guid itemId, RenameItemReq request, CancellationToken cancellationToken);
    Task<Result<MediaItemRes>> MoveItemAsync(Guid userId, Guid itemId, MoveItemReq request, CancellationToken cancellationToken);
    Task<Result<MediaItemRes>> CopyFileAsync(Guid userId, Guid itemId, CopyFileReq request, CancellationToken cancellationToken);
    Task<Result<MediaItemRes>> SetFavoriteAsync(Guid userId, Guid itemId, SetFavoriteReq request, CancellationToken cancellationToken);
    Task<Result<MediaItemRes>> TrashItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken);
    Task<Result<MediaItemRes>> RestoreItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken);
    Task<Result<ClearTrashRes>> ClearTrashAsync(Guid userId, ClearTrashReq request, CancellationToken cancellationToken);
    Task<Result<MediaItemRes>> RetryMetadataAsync(Guid userId, Guid itemId, CancellationToken cancellationToken);
}
