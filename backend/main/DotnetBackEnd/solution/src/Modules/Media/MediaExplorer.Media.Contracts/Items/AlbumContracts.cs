namespace MediaExplorer.Media.Contracts;

public sealed class AlbumRes
{
    public Guid Id { get; init; }
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public int MemberCount { get; init; }
    public Guid? CoverFileId { get; init; }
    public IReadOnlyList<Guid> CoverFileIds { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; init; }
    public string AccessRole { get; init; } = "owner";
    public string? SharedByDisplayName { get; init; }
    public DateTime? SharedAt { get; init; }
}

public sealed class AlbumsRes
{
    public IReadOnlyList<AlbumRes> Albums { get; init; } = [];
}

public sealed class AlbumDetailsRes
{
    public AlbumRes Album { get; init; } = null!;
    public IReadOnlyList<MediaItemRes> Items { get; init; } = [];
    public IReadOnlyList<int> AvailableYears { get; init; } = [];
    public string? NextCursor { get; init; }
}

public sealed record class BrowseAlbumItemsReq
{
    public string? Query { get; set; }
    public string Type { get; set; } = "all";
    public bool Favorite { get; set; }
    public string Sort { get; set; } = "added_desc";
    public int? Year { get; set; }
    public Guid? TagId { get; set; }
    public string? Cursor { get; set; }
    public int Limit { get; set; } = 100;
}

public sealed class CreateAlbumReq
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
}

public sealed class UpdateAlbumReq
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
}

public sealed class SetAlbumItemsReq
{
    public Guid[] ItemIds { get; set; } = [];
    public bool Included { get; set; }
}

public sealed class AlbumSharesRes
{
    public Guid AlbumId { get; init; }
    public IReadOnlyList<ItemShareRes> Shares { get; init; } = [];
}
