namespace MediaExplorer.Media.Contracts;

public sealed class BrowseItemsRes
{
    public IReadOnlyList<MediaItemRes> Items { get; init; } = [];
    public string? NextCursor { get; init; }
    public string? PreviousCursor { get; init; }
}

public sealed class PhotoTimelineYearsRes
{
    public IReadOnlyList<int> Years { get; init; } = [];
}

public sealed class PhotoTimelineAnchorsRes
{
    public IReadOnlyList<PhotoTimelineAnchorRes> Anchors { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class PhotoTimelineAnchorRes
{
    public int Year { get; init; }
    public int Month { get; init; }
    public int Count { get; init; }
    public string Cursor { get; init; } = null!;
}

public sealed class MediaItemRes
{
    public Guid Id { get; init; }
    public string Name { get; init; } = null!;
    public string Kind { get; init; } = null!;
    public string? MimeType { get; init; }
    public long? SizeBytes { get; init; }
    public Guid? ParentId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; init; }
    public DateTime? CapturedAt { get; init; }
    public string? CaptureDateSource { get; init; }
    public MediaMetadataRes? Metadata { get; init; }
    public bool Favorite { get; init; }
    public string LocalState { get; init; } = "remote";
    public string SyncState { get; init; } = "synced";
    public int? ChildCount { get; init; }
    public string? ThumbnailPath { get; init; }
    public string? CatalogPath { get; init; }
    public DateTime? TrashedAt { get; init; }
    public string? SystemRole { get; init; }
    public string AccessRole { get; init; } = "owner";
    public string? SharedByDisplayName { get; init; }
    public DateTime? SharedAt { get; init; }
    public IReadOnlyList<TagSummaryRes> Tags { get; init; } = [];
    public ItemCapabilitiesRes Capabilities { get; init; } = new();
}

public sealed class MediaMetadataRes
{
    public string State { get; init; } = "pending";
    public int AttemptCount { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? DetectedMimeType { get; init; }
    public string? MediaFormat { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Orientation { get; init; }
    public DateTime? EmbeddedCapturedAt { get; init; }
    public DateTime? FilenameCapturedAt { get; init; }
    public DateTime? ImportedCapturedAt { get; init; }
    public string? Description { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? AltitudeMeters { get; init; }
    public DateTime? ProcessedAt { get; init; }
    public string RecoveryManifestState { get; init; } = "pending";
}

public sealed class ItemCapabilitiesRes
{
    public bool CanRename { get; init; } = true;
    public bool CanMove { get; init; } = true;
    public bool CanTrash { get; init; } = true;
    public bool CanFavorite { get; init; } = true;
    public bool CanRetryMetadata { get; init; } = true;
    public bool CanShare { get; init; } = true;
}

public sealed class CreateFolderReq
{
    public string Name { get; set; } = null!;
    public Guid? ParentId { get; set; }
}

public sealed class RenameItemReq
{
    public string Name { get; set; } = null!;
}

public sealed class MoveItemReq
{
    public Guid? ParentId { get; set; }
}

public sealed class CopyFileReq
{
    public Guid? ParentId { get; set; }
}

public sealed class SetFavoriteReq
{
    public bool Favorite { get; set; }
}

/// <summary>Applies or clears the user-owned effective capture-date correction for catalog files.</summary>
public sealed class SetCaptureDateReq
{
    public Guid[] ItemIds { get; set; } = [];
    public DateOnly? CapturedOn { get; set; }
    public bool Reset { get; set; }
}

public sealed class ClearTrashReq
{
    public bool Confirmed { get; set; }
    public string? ConfirmationPhrase { get; set; }
    public int ExpectedFileCount { get; set; }
    public int ExpectedFolderCount { get; set; }
    public int ExpectedOriginalCount { get; set; }
    public long ExpectedOriginalBytes { get; set; }
}

public sealed class ClearTrashRes
{
    public int DeletedFileCount { get; init; }
    public int DeletedFolderCount { get; init; }
    public int DeletedOriginalCount { get; init; }
    public long DeletedOriginalBytes { get; init; }
    public int FailedOriginalCount { get; init; }
}

public sealed class TrashSummaryRes
{
    public int FileCount { get; init; }
    public int FolderCount { get; init; }
    public int OriginalCount { get; init; }
    public long OriginalBytes { get; init; }
}

public sealed class TrashPreviewReq
{
    public Guid[]? ItemIds { get; set; } = [];
}

public sealed class TrashPreviewRes
{
    public int SelectedRootCount { get; init; }
    public int FileCount { get; init; }
    public int FolderCount { get; init; }
    public int PhotoItemCount { get; init; }
    public int AlbumCount { get; init; }
}

public sealed class DownloadPreviewRes
{
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>Cached SQLite-derived storage totals for the authenticated member's library.</summary>
public sealed class StorageSummaryRes
{
    /// <summary>Catalog files in this member's library, including files in Trash.</summary>
    public long CatalogFileCount { get; init; }

    /// <summary>Distinct immutable originals referenced by this library, including files in Trash.</summary>
    public int UniqueOriginalCount { get; init; }
    public long StoredOriginalBytes { get; init; }

    /// <summary>Originals that the current Clear Trash action can actually reclaim.</summary>
    public int ReclaimableTrashOriginalCount { get; init; }
    public long ReclaimableTrashBytes { get; init; }
    public DateTime CalculatedAt { get; init; }
}
