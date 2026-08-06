namespace MediaExplorer.Media.Domain;

public sealed class Blob
{
    public const string AvailableLifecycleState = "available";
    public const string PendingPermanentDeletionLifecycleState = "pending_permanent_deletion";

    public string Sha256 { get; set; } = null!;
    public string ObjectKey { get; set; } = null!;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string LifecycleState { get; set; } = AvailableLifecycleState;
    public DateTime? DeletionRequestedAt { get; set; }
    public DateTime? DeletionLastAttemptAt { get; set; }
    public int DeletionAttemptCount { get; set; }
    public string? DeletionError { get; set; }
}
