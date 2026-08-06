namespace MediaExplorer.Media.Domain;

/// <summary>Links one file to one album without copying or moving the catalog item or original.</summary>
public sealed class AlbumItem
{
    public Guid AlbumId { get; set; }
    public Guid FileId { get; set; }
    public Guid AddedByUserId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
