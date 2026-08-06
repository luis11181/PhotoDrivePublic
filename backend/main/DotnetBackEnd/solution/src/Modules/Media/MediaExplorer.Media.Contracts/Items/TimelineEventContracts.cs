namespace MediaExplorer.Media.Contracts;

public sealed class TimelineEventRes
{
    public Guid Id { get; init; }
    public string Title { get; init; } = null!;
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; init; }
}

public sealed class TimelineEventsRes
{
    public IReadOnlyList<TimelineEventRes> Events { get; init; } = [];
}

public sealed class CreateTimelineEventReq
{
    public string Title { get; set; } = null!;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? Description { get; set; }
}

public sealed class UpdateTimelineEventReq
{
    public string Title { get; set; } = null!;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? Description { get; set; }
}
