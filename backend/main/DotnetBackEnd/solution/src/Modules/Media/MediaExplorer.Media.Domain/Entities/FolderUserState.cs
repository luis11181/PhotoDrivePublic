namespace MediaExplorer.Media.Domain;

public sealed class FolderUserState
{
    public Guid UserId { get; set; }
    public Guid FolderId { get; set; }
    public bool Favorite { get; set; }
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
