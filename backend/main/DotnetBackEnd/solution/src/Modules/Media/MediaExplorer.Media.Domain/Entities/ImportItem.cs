namespace MediaExplorer.Media.Domain;

public sealed class ImportItem
{
    public const string PendingState = "pending";
    public const string ProcessingState = "processing";
    public const string ImportedState = "imported";
    public const string SkippedState = "skipped";
    public const string FailedState = "failed";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ImportJobId { get; set; }
    public Guid? CatalogFileId { get; set; }
    public string SourceRelativePath { get; set; } = null!;
    public string? SidecarRelativePath { get; set; }
    public string Name { get; set; } = null!;
    public long ExpectedSizeBytes { get; set; }
    public DateTime? ImportedCapturedAt { get; set; }
    public string? ImportedDescription { get; set; }
    public double? ImportedLatitude { get; set; }
    public double? ImportedLongitude { get; set; }
    public double? ImportedAltitudeMeters { get; set; }
    public string? WarningCodes { get; set; }
    public string State { get; set; } = PendingState;
    public string? OutcomeCode { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}
