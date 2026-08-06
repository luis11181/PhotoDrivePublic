using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface ICacheBuildService
{
    Task<Result<CacheBuildOptionsRes>> GetOptionsAsync(CancellationToken cancellationToken);
    Task<Result<CacheBuildPreviewRes>> PreviewAsync(PreviewCacheBuildReq request, CancellationToken cancellationToken);
    Task<Result<CacheBuildJobRes>> StartAsync(Guid userId, StartCacheBuildReq request, CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<CacheBuildJobRes>>> ListAsync(CancellationToken cancellationToken);
    Task<Result<CacheBuildJobRes>> GetAsync(Guid jobId, CancellationToken cancellationToken);
    Task<Result<CacheBuildJobRes>> CancelAsync(Guid jobId, CancellationToken cancellationToken);
    Task<Result<CacheBuildJobRes>> RetryFailedAsync(Guid jobId, CancellationToken cancellationToken);
}
