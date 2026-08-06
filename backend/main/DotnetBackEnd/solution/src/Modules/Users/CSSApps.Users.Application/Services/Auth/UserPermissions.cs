using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Contracts.DTOs.Auth;
using CSSApps.Users.Contracts.Interfaces.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSSApps.Users.Application.Services.Auth;

// Services/UserPermissionsService.cs
internal sealed class UserPermissionsService : IUserPermissionsService
{
    private readonly IMemoryCache _cache;
    private readonly IUsersDbContext _dbContext;
    private readonly ILogger<UserPermissionsService> _logger;
    private readonly IOptions<UserPermissionsOptions> _options;

    public UserPermissionsService(
        IUsersDbContext dbContext,
        IMemoryCache cache,
        IOptions<UserPermissionsOptions> options,
        ILogger<UserPermissionsService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public async Task InvalidateUserPermissionsAsync(Guid userId)
    {
        _cache.Remove($"user_permissions_{userId}");
        await Task.CompletedTask;
    }

    public async Task<CachedUserPermissions?> GetUserPermissionsAsync(Guid userId)
    {
        var cacheKey = $"user_permissions_{userId}";

        var res = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow =
                TimeSpan.FromMinutes(_options.Value.CacheTimeInMinutes);

            try
            {
                var user = await _dbContext.Users
                    .Include(u => u.Roles)
                    .ThenInclude(r => r.Permissions)
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (user == null)
                {
                    _logger.LogWarning("[users.permissions] User {UserId} not found", userId);
                    return new CachedUserPermissions { UserId = userId };
                }

                return new CachedUserPermissions
                {
                    UserId = userId,
                    Roles = user.Roles.Select(r => r.Name).ToList(),
                    Permissions = user.Roles
                        .SelectMany(r => r.Permissions.Select(p => p.Name))
                        .Distinct()
                        .ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[users.permissions] Error fetching permissions for user {UserId}", userId);
                throw;
            }
        });

        return res;
    }

}