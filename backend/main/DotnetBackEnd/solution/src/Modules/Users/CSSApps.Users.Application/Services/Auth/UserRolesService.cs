using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Contracts.Interfaces.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSSApps.Users.Application.Services.Auth;

internal sealed class UserRolesService(
    IUsersDbContext dbContext,
    IMemoryCache cache,
    IOptions<UserRolesOptions> options,
    ILogger<UserRolesService> logger) : IUserRolesService
{
    public Task InvalidateUserRolesAsync(Guid userId)
    {
        cache.Remove(CacheKey(userId));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyCollection<string>?> GetUserRolesAsync(Guid userId) =>
        await cache.GetOrCreateAsync(CacheKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(options.Value.CacheTimeInMinutes);
            var roles = await dbContext.Users
                .Where(user => user.Id == userId)
                .SelectMany(user => user.Roles.Select(role => role.Name))
                .ToListAsync();
            if (roles.Count == 0) logger.LogWarning("[users.roles] User {UserId} has no roles or was not found", userId);
            return (IReadOnlyCollection<string>)roles;
        });

    private static string CacheKey(Guid userId) => $"user_roles_{userId}";
}
