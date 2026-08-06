namespace MediaExplorer.Media.Domain;

public sealed class CatalogFile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public long Sequence { get; set; }
    public Guid LibraryId { get; set; }
    public Guid? ParentFolderId { get; set; }
    public string Name { get; set; } = null!;
    public string MimeType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public DateTime CapturedAt { get; set; }
    public string? CaptureDateSource { get; set; }
    public DateTime? UserCapturedAt { get; set; }
    public DateTime? ImportedCapturedAt { get; set; }
    public DateTime? EmbeddedCapturedAt { get; set; }
    public DateTime? FilenameCapturedAt { get; set; }
    public string? Description { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? AltitudeMeters { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Orientation { get; set; }
    public string? MediaFormat { get; set; }
    public string MetadataState { get; set; } = "pending";
    public int MetadataAttemptCount { get; set; }
    public string? MetadataErrorCode { get; set; }
    public string? MetadataErrorMessage { get; set; }
    public DateTime? MetadataProcessedAt { get; set; }
    public string RecoveryManifestState { get; set; } = "pending";
    public Guid CurrentVersionId { get; set; }
    public DateTime? TrashedAt { get; set; }
    public Guid? TrashRootId { get; set; }
}
