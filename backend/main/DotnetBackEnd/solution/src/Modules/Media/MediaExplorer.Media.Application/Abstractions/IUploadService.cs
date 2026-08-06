using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface IUploadService
{
    Task<Result<UploadNameConflictsRes>> CheckNameConflictsAsync(Guid userId, CheckUploadNameConflictsReq request, CancellationToken cancellationToken);
    Task<Result<CreateUploadRes>> CreateUploadAsync(Guid userId, CreateUploadReq request, CancellationToken cancellationToken);
    Task<Result<UploadSessionRes>> GetUploadSessionAsync(Guid userId, Guid uploadId, CancellationToken cancellationToken);
    Task<Result<UploadProgressRes>> AppendUploadChunkAsync(Guid userId, Guid uploadId, long start, long end, long total, Stream content, CancellationToken cancellationToken);
    Task<Result<CompleteUploadRes>> CompleteUploadAsync(Guid userId, Guid uploadId, CompleteUploadReq request, CancellationToken cancellationToken);
    Task<Result> CancelUploadAsync(Guid userId, Guid uploadId, CancellationToken cancellationToken);
}
