using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;
namespace MediaExplorer.Media.Application;

public interface IStorageUsageService
{
    Task<Result<StorageUsageRes>> GetAsync(Guid userId, bool refresh, CancellationToken cancellationToken);
    Task<Result<StorageFilesRes>> FilesAsync(Guid userId, string category, string? month, Guid? folderId, int offset, CancellationToken cancellationToken);
}
