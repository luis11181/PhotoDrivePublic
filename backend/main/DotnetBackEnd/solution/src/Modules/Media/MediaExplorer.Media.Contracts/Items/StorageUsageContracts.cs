namespace MediaExplorer.Media.Contracts;

public sealed record StorageBucket(string Key, long Bytes, long Count);
public sealed record StorageFolder(Guid Id, string Name, long Bytes);
public sealed record StorageFile(Guid Id, string Name, long Bytes, bool Trashed, Guid VersionId, bool Current);
public sealed record StorageUsageRes(long PhysicalBytes, long LogicalBytes, long TrashBytes, long RetainedVersionBytes,
    long VideoBytes, IReadOnlyList<StorageBucket> Growth, IReadOnlyList<StorageFolder> Folders, DateTime CalculatedAt);
public sealed record StorageFilesRes(IReadOnlyList<StorageFile> Files, bool HasMore);
