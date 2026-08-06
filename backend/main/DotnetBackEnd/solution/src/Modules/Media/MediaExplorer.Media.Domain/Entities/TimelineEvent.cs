namespace MediaExplorer.Media.Domain;

/// <summary>A library-owned calendar annotation whose photos are derived from capture dates.</summary>
public sealed class TimelineEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LibraryId { get; set; }
    public string Title { get; set; } = null!;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? Description { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
