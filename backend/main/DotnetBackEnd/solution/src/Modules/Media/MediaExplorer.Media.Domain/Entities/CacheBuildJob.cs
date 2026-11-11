namespace MediaExplorer.Media.Domain;

public sealed class CacheBuildJob
{
    public const string PendingState = "pending";
    public const string RunningState = "running";
    public const string CancellingState = "cancelling";
    public const string CancelledState = "cancelled";
    public const string CompletedState = "completed";
    public const string CompletedWithErrorsState = "completed_with_errors";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RequestedByUserId { get; set; }
    /// <summary>The host whose local cache this job fills; only that host processes it.</summary>
    public Guid? HostId { get; set; }
    public bool KeepOriginals { get; set; } = true;
    public string State { get; set; } = PendingState;
    public int SelectedItemCount { get; set; }
    public int QueuedItemCount { get; set; }
    public long PlannedDownloadBytes { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
