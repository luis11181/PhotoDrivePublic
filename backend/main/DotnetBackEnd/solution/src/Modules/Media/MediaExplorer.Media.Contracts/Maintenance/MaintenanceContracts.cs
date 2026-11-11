namespace MediaExplorer.Media.Contracts;

public sealed class DeveloperMaintenanceReportRes
{
    public DateTime RanAt { get; init; }
    public long CatalogFileCount { get; init; }
    public long CatalogFolderCount { get; init; }
    public int ReferencedBlobCount { get; init; }
    public long ReferencedBlobBytes { get; init; }
    public int MissingOriginalCount { get; init; }
    public int OrphanedCatalogBlobCount { get; init; }
    public long OrphanedCatalogBlobBytes { get; init; }
    public int PendingPermanentDeletionBlobCount { get; init; }
    public long PendingPermanentDeletionBytes { get; init; }
    public int UntrackedS3OriginalCount { get; init; }
    public int PendingCleanupCandidateCount { get; init; }
    public int RemovedCompletedStagingFiles { get; init; }
    public long RemovedCompletedStagingBytes { get; init; }
    public int OriginalCacheEntryCount { get; init; }
    public long OriginalCacheBytes { get; init; }
    public long OriginalCacheLimitBytes { get; init; }
    public int ThumbnailCacheEntryCount { get; init; }
    public long ThumbnailCacheBytes { get; init; }
    public int EvictedCacheEntries { get; init; }
    public long EvictedCacheBytes { get; init; }
    public int S3OriginalKeyCount { get; init; }
    public int S3OriginalVersionCount { get; init; }
    public long S3OriginalVersionBytes { get; init; }
    public int S3RecoveryManifestKeyCount { get; init; }
    public int S3RecoveryManifestVersionCount { get; init; }
    public long S3RecoveryManifestVersionBytes { get; init; }
    public int S3DeleteMarkerCount { get; init; }
    public IReadOnlyList<StorageCleanupCandidateRes> CleanupCandidates { get; init; } = [];
    public IReadOnlyList<DeveloperMaintenanceIssueRes> Issues { get; init; } = [];
}

public sealed class DeveloperBackupReportRes
{
    public DateTime RanAt { get; init; }
    public long LibraryCount { get; init; }
    public long FolderCount { get; init; }
    public long FileCount { get; init; }
    public long BlobCount { get; init; }
    public long FileVersionCount { get; init; }
    public long FileVersionMetadataCount { get; init; }
    public long UserCount { get; init; }
    public long RoleCount { get; init; }
    public long UserSessionCount { get; init; }
    public long UserTokenCount { get; init; }
    public string? RemoteBackupSetId { get; init; }
    public int RemoteBackupArtifactCount { get; init; }
    public long RemoteBackupBytes { get; init; }
    public bool RemoteBackupVerified { get; init; }
    public IReadOnlyList<DeveloperMaintenanceIssueRes> Issues { get; init; } = [];
}

public sealed class DeveloperBackupStatusRes
{
    public DateTimeOffset? LastRemoteVerifiedAt { get; init; }
    public double? AgeHours { get; init; }
    public int FreshnessWindowHours { get; init; }
    public string State { get; init; } = null!;
}

public sealed class DeveloperOperationalHealthRes
{
    public DateTime CheckedAt { get; init; }
    public string State { get; init; } = "healthy";
    public DateTimeOffset? LastRemoteVerifiedBackupAt { get; init; }
    public DateTimeOffset? LastBackupFailureAt { get; init; }
    public int ConsecutiveBackupFailureCount { get; init; }
    public int PendingUploadCount { get; init; }
    public int StaleUploadCount { get; init; }
    public int PendingMetadataCount { get; init; }
    public int StaleMetadataCount { get; init; }
    public int FailedMetadataCount { get; init; }
    public int RepeatedWorkerFailureCount { get; init; }
    public long AvailableDiskBytes { get; init; }
    public long OriginalCacheBytes { get; init; }
    public long OriginalCacheMaxBytes { get; init; }
    public bool LowDiskSpace { get; init; }
    public bool CachePressure { get; init; }
    /// <summary>Whether this host runs installation-wide background work (metadata extraction, scheduled backups).</summary>
    public bool IsPrimaryHost { get; init; }
    /// <summary>Last successful pull of this host's local database replica; null for a local test database.</summary>
    public DateTimeOffset? DatabaseLastPullAt { get; init; }
    public DateTimeOffset? DatabaseLastCloudFailureAt { get; init; }
    public IReadOnlyList<DeveloperMaintenanceIssueRes> Issues { get; init; } = [];
}

public sealed class StorageCleanupCandidateRes
{
    public Guid Id { get; init; }
    public string Kind { get; init; } = null!;
    public long SizeBytes { get; init; }
    public DateTime DetectedAt { get; init; }
    public int AttemptCount { get; init; }
    public string? Error { get; init; }
}

public sealed class CleanupStorageInconsistenciesReq
{
    public bool Confirmed { get; set; }
    public string? ConfirmationPhrase { get; set; }
    public int ExpectedCandidateCount { get; set; }
    public long? ExpectedCandidateBytes { get; set; }
    public List<Guid> CandidateIds { get; set; } = [];
}

public sealed class CleanupStorageInconsistenciesRes
{
    public string? SafetyBackupSetId { get; init; }
    public int DeletedCandidateCount { get; init; }
    public int ResolvedCandidateCount { get; init; }
    public int FailedCandidateCount { get; init; }
    public IReadOnlyList<StorageCleanupCandidateRes> RemainingCandidates { get; init; } = [];
}

public sealed class DeveloperMaintenanceIssueRes
{
    public string Severity { get; init; } = null!;
    public string Code { get; init; } = null!;
    public string Message { get; init; } = null!;
}

/// <summary>The PhotoDrive hosts sharing this installation's database.</summary>
public sealed class ClusterHostsRes
{
    public Guid CurrentHostId { get; init; }
    public Guid? PrimaryHostId { get; init; }
    public DateTime? PrimaryChangedAt { get; init; }
    public IReadOnlyList<ClusterHostRes> Hosts { get; init; } = [];
}

public sealed class ClusterHostRes
{
    public Guid Id { get; init; }
    public string Name { get; init; } = null!;
    public DateTime FirstSeenAt { get; init; }
    public DateTime LastSeenAt { get; init; }
    /// <summary>The host sent a heartbeat within the last few minutes.</summary>
    public bool Online { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsPrimary { get; init; }
}

public sealed class SetPrimaryHostReq
{
    public Guid HostId { get; init; }
}
