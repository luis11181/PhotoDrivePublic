namespace MediaExplorer.Media.Domain;

/// <summary>
/// Grants one family user viewer access, or collaborator access to an album. Folder grants inherit through
/// the live hierarchy and album grants expose current members; no catalog item or original is copied.
/// </summary>
public sealed class CatalogShare
{
    public const string ViewerRole = "viewer";
    public const string CollaboratorRole = "collaborator";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LibraryId { get; set; }
    public Guid? FileId { get; set; }
    public Guid? FolderId { get; set; }
    public Guid? AlbumId { get; set; }
    public Guid RecipientUserId { get; set; }
    public Guid GrantedByUserId { get; set; }
    public string Role { get; set; } = ViewerRole;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
