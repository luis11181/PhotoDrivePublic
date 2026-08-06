namespace MediaExplorer.Media.Domain;

public sealed class FolderTag
{
    public Guid FolderId { get; set; }
    public Guid TagId { get; set; }
    public Guid AddedByUserId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
