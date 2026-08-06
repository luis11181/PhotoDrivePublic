namespace MediaExplorer.Media.Contracts;

public sealed class CacheBuildOptionsRes
{
    public long OriginalCacheMaxBytes { get; init; }
    public long OriginalCacheBytes { get; init; }
    public int OriginalCacheEntryCount { get; init; }
    public long AvailableDiskBytes { get; init; }
    public long CatalogFileCount { get; init; }
    public long StoredOriginalBytes { get; init; }
    public int CatalogMediaItemCount { get; init; }
    public int DistinctOriginalCount { get; init; }
    public int GridThumbnailCount { get; init; }
    public int CachedOriginalCount { get; init; }
}

public class PreviewCacheBuildReq
{
    public bool KeepOriginals { get; set; } = true;
}

public sealed class StartCacheBuildReq : PreviewCacheBuildReq
{
    public int ExpectedSelectedItemCount { get; set; }
    public int ExpectedQueuedItemCount { get; set; }
    public long ExpectedDownloadBytes { get; set; }
}

public sealed class CacheBuildPreviewRes
{
    public int SelectedItemCount { get; init; }
    public int QueuedItemCount { get; init; }
    public int MissingThumbnailCount { get; init; }
    public int MissingOriginalCount { get; init; }
    public long DownloadBytes { get; init; }
    public bool KeepOriginals { get; init; }
    public long OriginalCacheMaxBytes { get; init; }
    public long OriginalCacheBytes { get; init; }
    public long AvailableDiskBytes { get; init; }
    public bool FitsConfiguredCache { get; init; }
}

public sealed class CacheBuildJobRes
{
    public Guid Id { get; init; }
    public bool KeepOriginals { get; init; }
    public string State { get; init; } = null!;
    public int SelectedItemCount { get; init; }
    public int QueuedItemCount { get; init; }
    public int PendingCount { get; init; }
    public int ProcessingCount { get; init; }
    public int CompletedCount { get; init; }
    public int SkippedCount { get; init; }
    public int FailedCount { get; init; }
    public int CancelledCount { get; init; }
    public long PlannedDownloadBytes { get; init; }
    public string? Error { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}
