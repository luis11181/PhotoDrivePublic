namespace MediaExplorer.Media.Domain;

/// <summary>Durable, developer-reviewed cleanup work for storage inconsistencies outside normal Trash deletion.</summary>
public sealed class StorageCleanupCandidate
{
    public const string PendingState = "pending";
    public const string OrphanedCatalogBlobKind = "orphaned_catalog_blob";
    public const string UntrackedS3OriginalKind = "untracked_s3_original";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string BlobSha256 { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public long SizeBytes { get; set; }
    public string State { get; set; } = PendingState;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? Error { get; set; }
}
