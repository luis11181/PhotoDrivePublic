using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Contracts;

namespace MediaExplorer.Media.Application;

public interface ITimelineEventService
{
    Task<Result<TimelineEventsRes>> ListAsync(Guid userId, string? query, int limit, CancellationToken cancellationToken);
    Task<Result<TimelineEventRes>> GetAsync(Guid userId, Guid eventId, CancellationToken cancellationToken);
    Task<Result<TimelineEventRes>> CreateAsync(Guid userId, CreateTimelineEventReq request, CancellationToken cancellationToken);
    Task<Result<TimelineEventRes>> UpdateAsync(Guid userId, Guid eventId, UpdateTimelineEventReq request, CancellationToken cancellationToken);
    Task<Result> DeleteAsync(Guid userId, Guid eventId, CancellationToken cancellationToken);
}
