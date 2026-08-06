using CSSApps.Users.Infrastructure.Data;
using MediaExplorer.Media.Application;
using Microsoft.EntityFrameworkCore;

namespace CSSApps.Api.DependencyInjection;

internal sealed class FamilyUploaderIdentityDirectory(UsersDbContext dbContext) : IUploaderIdentityDirectory
{
    public Task<string> GetEmailAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Users.AsNoTracking()
            .Where(user => user.Id == userId && !user.IsServiceAccount)
            .Select(user => user.Email)
            .SingleAsync(cancellationToken);
}
