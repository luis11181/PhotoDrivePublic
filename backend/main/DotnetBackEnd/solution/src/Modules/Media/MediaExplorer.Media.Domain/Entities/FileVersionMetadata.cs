namespace MediaExplorer.Media.Domain;

/// <summary>Extracted facts and durable processing state for one immutable file version.</summary>
public sealed class FileVersionMetadata
{
    public Guid FileVersionId { get; set; }
    public string State { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? ExtractorVersion { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? DetectedMimeType { get; set; }
    public string? MediaFormat { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Orientation { get; set; }
    public DateTime? ExifDateTimeOriginal { get; set; }
    public DateTime? ExifDateTimeDigitized { get; set; }
    public DateTime? EmbeddedCreatedAt { get; set; }
    public DateTime? FilenameCapturedAt { get; set; }
    public DateTime? ImportedCapturedAt { get; set; }
    public string? ImportedDescription { get; set; }
    public double? ImportedLatitude { get; set; }
    public double? ImportedLongitude { get; set; }
    public double? ImportedAltitudeMeters { get; set; }
    public DateTime EffectiveCapturedAt { get; set; }
    public string CaptureDateSource { get; set; } = "uploaded_at";
    public string? RawMetadataJson { get; set; }
    public string RecoveryManifestState { get; set; } = "pending";
    public string? RecoveryManifestError { get; set; }
    public DateTime? RecoveryManifestWrittenAt { get; set; }
}
