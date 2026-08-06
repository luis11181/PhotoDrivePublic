namespace MediaExplorer.Media.Contracts;

public sealed class BatchItemsReq
{
    public string Action { get; set; } = null!;
    public Guid[] ItemIds { get; set; } = [];
    public bool? Favorite { get; set; }
    public Guid? ParentId { get; set; }
}

public sealed class BatchItemsRes
{
    public int RequestedCount { get; init; }
    public int SucceededCount { get; init; }
    public int UnchangedCount { get; init; }
    public int SkippedCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyList<BatchItemResultRes> Results { get; init; } = [];
}

public sealed class BatchItemResultRes
{
    public Guid ItemId { get; init; }
    public string Status { get; init; } = null!;
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}
