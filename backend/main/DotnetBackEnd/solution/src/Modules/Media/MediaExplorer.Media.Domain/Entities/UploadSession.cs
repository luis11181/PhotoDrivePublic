namespace MediaExplorer.Media.Domain;

public sealed class UploadSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid LibraryId { get; set; }
    public Guid? ParentFolderId { get; set; }
    public Guid? CatalogFileId { get; set; }
    public string Name { get; set; } = null!;
    public string MimeType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public string StagingFileName { get; set; } = null!;
    public string State { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
