namespace MediaExplorer.Media.Domain;

public sealed class FileVersion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid FileId { get; set; }
    public string BlobSha256 { get; set; } = null!;
    public long SizeBytes { get; set; }
    public string MimeType { get; set; } = null!;
    public string DeclaredMimeType { get; set; } = null!;
    public string OriginalName { get; set; } = null!;
    public Guid UploaderUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
