namespace MediaExplorer.Media.Domain;

public sealed class FileUserState
{
    public Guid UserId { get; set; }
    public Guid FileId { get; set; }
    public bool Favorite { get; set; }
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
