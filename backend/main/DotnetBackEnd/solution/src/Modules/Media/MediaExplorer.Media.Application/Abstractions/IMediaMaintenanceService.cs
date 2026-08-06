using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface IMediaMaintenanceService
{
    Task<Result<DeveloperMaintenanceReportRes>> AuditAsync(CancellationToken cancellationToken);
    Task<Result<DeveloperBackupStatusRes>> GetBackupStatusAsync(CancellationToken cancellationToken);
    Task<Result<DeveloperOperationalHealthRes>> GetOperationalHealthAsync(CancellationToken cancellationToken);
    Task<Result<DeveloperBackupReportRes>> BackupAsync(CancellationToken cancellationToken);
    Task<Result<CleanupStorageInconsistenciesRes>> CleanupAsync(CleanupStorageInconsistenciesReq request, CancellationToken cancellationToken);
}
