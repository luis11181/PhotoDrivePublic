using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface IAlbumService
{
    Task<Result<AlbumsRes>> ListAsync(Guid userId, string scope, CancellationToken cancellationToken);
    Task<Result<AlbumDetailsRes>> GetAsync(Guid userId, Guid albumId, BrowseAlbumItemsReq request, CancellationToken cancellationToken);
    Task<Result<AlbumRes>> CreateAsync(Guid userId, CreateAlbumReq request, CancellationToken cancellationToken);
    Task<Result<AlbumRes>> UpdateAsync(Guid userId, Guid albumId, UpdateAlbumReq request, CancellationToken cancellationToken);
    Task<Result> DeleteAsync(Guid userId, Guid albumId, CancellationToken cancellationToken);
    Task<Result> AddItemAsync(Guid userId, Guid albumId, Guid fileId, CancellationToken cancellationToken);
    Task<Result> RemoveItemAsync(Guid userId, Guid albumId, Guid fileId, CancellationToken cancellationToken);
    Task<Result<BatchItemsRes>> SetItemsAsync(Guid userId, Guid albumId, SetAlbumItemsReq request, CancellationToken cancellationToken);
    Task<Result<AlbumSharesRes>> ListSharesAsync(Guid userId, Guid albumId, CancellationToken cancellationToken);
    Task<Result<ItemShareRes>> SetShareAsync(Guid userId, Guid albumId, Guid recipientUserId, SetItemShareReq request, CancellationToken cancellationToken);
    Task<Result> RevokeShareAsync(Guid userId, Guid albumId, Guid recipientUserId, CancellationToken cancellationToken);
}
