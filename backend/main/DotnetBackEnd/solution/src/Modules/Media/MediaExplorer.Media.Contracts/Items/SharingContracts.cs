namespace MediaExplorer.Media.Contracts;

public sealed class ShareRecipientRes
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = null!;
    public string Email { get; init; } = null!;
}

public sealed class ShareRecipientsRes
{
    public IReadOnlyList<ShareRecipientRes> Recipients { get; init; } = [];
}

public sealed class ItemShareRes
{
    public Guid RecipientUserId { get; init; }
    public string RecipientDisplayName { get; init; } = null!;
    public string RecipientEmail { get; init; } = null!;
    public string Role { get; init; } = "viewer";
    public DateTime SharedAt { get; init; }
}

public sealed class ItemSharesRes
{
    public Guid ItemId { get; init; }
    public IReadOnlyList<ItemShareRes> Shares { get; init; } = [];
}

public sealed class SetItemShareReq
{
    public string Role { get; set; } = "viewer";
}
