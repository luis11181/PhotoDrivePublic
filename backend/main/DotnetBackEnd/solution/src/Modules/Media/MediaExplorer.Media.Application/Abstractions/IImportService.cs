using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface IImportService
{
    Task<Result<ImportOptionsRes>> GetOptionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<ImportJobRes>>> ListAsync(Guid userId, CancellationToken cancellationToken);
    Task<Result<ImportJobRes>> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken);
    Task<Result<ImportItemResultsRes>> ListItemsAsync(Guid userId, Guid jobId, string category, string? cursor, int limit, CancellationToken cancellationToken);
    Task<Result<ImportJobRes>> StartGoogleTakeoutAsync(Guid userId, CreateGoogleTakeoutImportReq request, CancellationToken cancellationToken);
    Task<Result<ImportJobRes>> RetryFailedAsync(Guid userId, Guid jobId, CancellationToken cancellationToken);
}
