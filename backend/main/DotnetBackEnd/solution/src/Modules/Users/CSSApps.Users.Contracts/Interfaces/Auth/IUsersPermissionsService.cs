using CSSApps.Users.Contracts.DTOs.Auth;

namespace CSSApps.Users.Contracts.Interfaces.Auth;

public interface IUserPermissionsService
{
    Task<CachedUserPermissions?> GetUserPermissionsAsync(Guid userId);
    Task InvalidateUserPermissionsAsync(Guid userId);
}