namespace MediaExplorer.Media.Application;

public sealed record FileQueryOptions
{
    public string? Query { get; init; }
    public string Location { get; init; } = "children";
    public Guid? FolderId { get; init; }
    public bool IncludePhotos { get; init; }
    public bool Favorite { get; init; }
    public string Type { get; init; } = "all";
    public string Sort { get; init; } = "name_asc";
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 60;
    public Guid? TagId { get; init; }
}
