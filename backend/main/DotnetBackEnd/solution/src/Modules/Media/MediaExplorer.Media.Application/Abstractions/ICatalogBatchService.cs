using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface ICatalogBatchService
{
    Task<Result<BatchItemsRes>> ExecuteAsync(Guid userId, BatchItemsReq request, CancellationToken cancellationToken);
    Task<Result<BatchItemsRes>> SetCaptureDateAsync(Guid userId, SetCaptureDateReq request, CancellationToken cancellationToken);
}
