namespace MediaExplorer.Media.Domain;

public sealed class CacheBuildItem
{
    public const string PendingState = "pending";
    public const string ProcessingState = "processing";
    public const string CompletedState = "completed";
    public const string SkippedState = "skipped";
    public const string FailedState = "failed";
    public const string CancelledState = "cancelled";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CacheBuildJobId { get; set; }
    public Guid CatalogFileId { get; set; }
    public string BlobSha256 { get; set; } = null!;
    public string MimeType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public string State { get; set; } = PendingState;
    public int AttemptCount { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}
