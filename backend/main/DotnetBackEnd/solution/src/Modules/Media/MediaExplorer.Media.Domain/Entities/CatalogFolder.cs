namespace MediaExplorer.Media.Domain;

public sealed class CatalogFolder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public long Sequence { get; set; }
    public Guid LibraryId { get; set; }
    public Guid? ParentFolderId { get; set; }
    public string Name { get; set; } = null!;
    public string? SystemRole { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public DateTime? TrashedAt { get; set; }
    public Guid? TrashRootId { get; set; }
}
