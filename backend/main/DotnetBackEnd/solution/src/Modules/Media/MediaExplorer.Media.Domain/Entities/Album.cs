namespace MediaExplorer.Media.Domain;

/// <summary>A library-owned, catalog-only collection of Photos items.</summary>
public sealed class Album
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LibraryId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
