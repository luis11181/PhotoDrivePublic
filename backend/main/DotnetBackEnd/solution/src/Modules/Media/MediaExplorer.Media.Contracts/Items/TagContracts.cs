namespace MediaExplorer.Media.Contracts;

public sealed class TagRes
{
    public Guid Id { get; init; }
    public string Name { get; init; } = null!;
    public string Color { get; init; } = null!;
    public int FileCount { get; init; }
    public int FolderCount { get; init; }
}

public sealed class TagSummaryRes
{
    public Guid Id { get; init; }
    public string Name { get; init; } = null!;
    public string Color { get; init; } = null!;
}

public sealed class CreateTagReq
{
    public string Name { get; set; } = null!;
    public string Color { get; set; } = "gray";
}

public sealed class UpdateTagReq
{
    public string Name { get; set; } = null!;
    public string Color { get; set; } = "gray";
}

public sealed class SetTagItemsReq
{
    public Guid[] ItemIds { get; set; } = [];
    public bool Included { get; set; }
}
