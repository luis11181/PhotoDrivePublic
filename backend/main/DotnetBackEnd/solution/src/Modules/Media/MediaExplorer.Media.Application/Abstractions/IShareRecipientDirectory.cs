namespace MediaExplorer.Media.Application;

public sealed record ShareRecipientProfile(Guid UserId, string DisplayName, string Email);

/// <summary>Host-provided bridge to the separate Users database.</summary>
public interface IShareRecipientDirectory
{
    Task<IReadOnlyList<ShareRecipientProfile>> ListEligibleAsync(Guid requesterUserId, CancellationToken cancellationToken);
    Task<ShareRecipientProfile?> FindEligibleAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, ShareRecipientProfile>> FindByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}
