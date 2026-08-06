using CSSApps.Users.Domain.Entities.UserAggregate;
using CSSApps.Users.Infrastructure.Data;
using MediaExplorer.Media.Application;
using Microsoft.EntityFrameworkCore;

namespace CSSApps.Api.DependencyInjection;

internal sealed class FamilyShareRecipientDirectory(UsersDbContext dbContext) : IShareRecipientDirectory
{
    public async Task<IReadOnlyList<ShareRecipientProfile>> ListEligibleAsync(Guid requesterUserId, CancellationToken cancellationToken) =>
        await dbContext.Users.AsNoTracking()
            .Where(user => user.Id != requesterUserId && !user.IsServiceAccount && user.AccountStatus != HumanAccountStatus.Disabled)
            .OrderBy(user => user.Name.LastName).ThenBy(user => user.Name.FirstName).ThenBy(user => user.Email)
            .Select(user => new ShareRecipientProfile(user.Id, user.Name.FullName, user.Email))
            .ToListAsync(cancellationToken);

    public Task<ShareRecipientProfile?> FindEligibleAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Users.AsNoTracking()
            .Where(user => user.Id == userId && !user.IsServiceAccount && user.AccountStatus != HumanAccountStatus.Disabled)
            .Select(user => new ShareRecipientProfile(user.Id, user.Name.FullName, user.Email))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, ShareRecipientProfile>> FindByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        await dbContext.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id) && !user.IsServiceAccount)
            .Select(user => new ShareRecipientProfile(user.Id, user.Name.FullName, user.Email))
            .ToDictionaryAsync(user => user.UserId, cancellationToken);
}
