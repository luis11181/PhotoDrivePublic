using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface ITagService
{
    Task<Result<IReadOnlyList<TagRes>>> ListAsync(Guid userId, CancellationToken cancellationToken);
    Task<Result<TagRes>> CreateAsync(Guid userId, CreateTagReq request, CancellationToken cancellationToken);
    Task<Result<TagRes>> UpdateAsync(Guid userId, Guid tagId, UpdateTagReq request, CancellationToken cancellationToken);
    Task<Result> DeleteAsync(Guid userId, Guid tagId, CancellationToken cancellationToken);
    Task<Result<BatchItemsRes>> SetItemsAsync(Guid userId, Guid tagId, SetTagItemsReq request, CancellationToken cancellationToken);
}
