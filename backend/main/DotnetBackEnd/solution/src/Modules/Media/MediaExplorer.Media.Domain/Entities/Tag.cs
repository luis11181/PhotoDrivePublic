namespace MediaExplorer.Media.Domain;

/// <summary>A library-owned catalog label. Tags never affect original storage.</summary>
public sealed class Tag
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LibraryId { get; set; }
    public string Name { get; set; } = null!;
    public string NormalizedName { get; set; } = null!;
    public string Color { get; set; } = "gray";
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
