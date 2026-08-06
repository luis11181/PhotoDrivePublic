namespace MediaExplorer.Media.Domain;

public sealed class ImportJob
{
    public const string GoogleTakeoutSourceKind = "google_photos_takeout";
    public const string RunningState = "running";
    public const string CompletedState = "completed";
    public const string CompletedWithErrorsState = "completed_with_errors";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid LibraryId { get; set; }
    public Guid? TargetFolderId { get; set; }
    public string? TargetFolderPath { get; set; }
    public string SourceKind { get; set; } = GoogleTakeoutSourceKind;
    public string SourcePath { get; set; } = null!;
    public string NameConflictPolicy { get; set; } = "skip";
    public string State { get; set; } = RunningState;
    public int OrphanSidecarCount { get; set; }
    public int IgnoredJsonCount { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
