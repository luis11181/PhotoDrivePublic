namespace MediaExplorer.Media.Contracts;

public sealed class CreateUploadReq
{
    public Guid? ClientUploadId { get; set; }
    public string Name { get; set; } = null!;
    public string? MimeType { get; set; }
    public long SizeBytes { get; set; }
    public Guid? ParentId { get; set; }
    public bool UseDefaultMediaFolder { get; set; }
    public bool UseDefaultDocumentsFolder { get; set; }
    public bool AllowNameConflict { get; set; }
}

public sealed class CheckUploadNameConflictsReq
{
    public List<string> Names { get; set; } = [];
    public Guid? ParentId { get; set; }
    public bool UseDefaultMediaFolder { get; set; }
    public bool UseDefaultDocumentsFolder { get; set; }
}

public sealed class UploadNameConflictsRes
{
    public List<string> Names { get; init; } = [];
}

public sealed class CreateUploadRes
{
    public Guid Id { get; init; }
    public int ChunkSizeBytes { get; init; }
    public long ReceivedBytes { get; init; }
}

public sealed class UploadProgressRes
{
    public Guid Id { get; init; }
    public long ReceivedBytes { get; init; }
    public long SizeBytes { get; init; }
}

public sealed class UploadSessionRes
{
    public Guid Id { get; init; }
    public int ChunkSizeBytes { get; init; }
    public long ReceivedBytes { get; init; }
    public long SizeBytes { get; init; }
    public string State { get; init; } = null!;
}

public sealed class CompleteUploadReq
{
    public string Sha256 { get; set; } = null!;
    public bool AllowNameConflict { get; set; }
}

public sealed class CompleteUploadRes
{
    public MediaItemRes Item { get; init; } = null!;
    public string DuplicateState { get; init; } = "none";
}
