using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface ISharingService
{
    Task<Result<BrowseItemsRes>> BrowseSharedAsync(Guid userId, Guid? parentId, string? cursor, int limit, string type, string sort, CancellationToken cancellationToken);
    Task<Result<BrowseItemsRes>> SearchSharedAsync(Guid userId, string query, string type, string? cursor, int limit, Guid? tagId, CancellationToken cancellationToken);
    Task<Result<ShareRecipientsRes>> ListRecipientsAsync(Guid userId, CancellationToken cancellationToken);
    Task<Result<ItemSharesRes>> ListItemSharesAsync(Guid userId, Guid itemId, CancellationToken cancellationToken);
    Task<Result<ItemShareRes>> SetItemShareAsync(Guid userId, Guid itemId, Guid recipientUserId, SetItemShareReq request, CancellationToken cancellationToken);
    Task<Result> RevokeItemShareAsync(Guid userId, Guid itemId, Guid recipientUserId, CancellationToken cancellationToken);
}
