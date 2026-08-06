using System.Security.Claims;
using CSSApps.Shared.Contracts.Constants;
using CSSApps.Shared.Utils.Common.Auth;
using CSSApps.Users.Contracts.DTOs.Auth;
using CSSApps.Users.Contracts.Interfaces.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSSApps.Users.Infrastructure.Services.Auth;

public class AddUserPermissionsClaimsTransformation : IClaimsTransformation
{
    private readonly ILogger<AddUserPermissionsClaimsTransformation> _logger;
    private readonly IServiceProvider _serviceProvider;

    public AddUserPermissionsClaimsTransformation(
        IServiceProvider serviceProvider,
        ILogger<AddUserPermissionsClaimsTransformation> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <Summary>
    /// Transforms the given ClaimsPrincipal by adding user permissions and roles as claims.
    /// Because it implements IClaimsTransformation, the Authentication Middleware automatically calls this TransformAsync method on every single authenticated HTTP request.
    /// </Summary>
    /// <param name="principal">The original ClaimsPrincipal.</param>
    /// <returns>The transformed ClaimsPrincipal with added permissions and roles claims.</returns>
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        try
        {
            // Early exits
            if (!principal.Identity?.IsAuthenticated ?? true) return principal;

            if (HasRequiredClaims(principal)) return principal;

            var userId = AuthUtils.GetUserIdentifier(principal);
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("[users.permissions] No user identifier found in claims");
                return principal;
            }

            // Create scope to resolve services
            using var scope = _serviceProvider.CreateScope();
            var permissionsService = scope.ServiceProvider.GetRequiredService<IUserPermissionsService>();

            if (permissionsService == null)
            {
                _logger.LogError("[users.permissions] IUserPermissionsService not registered in DI container");
                return principal;
            }
            var permissions = await GetUserPermissions(permissionsService, userId);
            if (permissions == null) return principal;

            EnrichClaimsIdentity(principal, permissions);

            return principal;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.permissions] Claims transformation failed");
            return principal;
        }
    }

    private static bool HasRequiredClaims(ClaimsPrincipal principal)
    {
        return principal.HasClaim(c => c.Type == AuthCommonConstants.CustomClaims.Permission)
               && principal.HasClaim(c => c.Type == ClaimTypes.Role);
    }


    private async Task<CachedUserPermissions?> GetUserPermissions(
        IUserPermissionsService permissionsService,
        string userId)
    {
        try
        {
            // Try parse as Guid first
            if (Guid.TryParse(userId, out var guidId))
                return await permissionsService.GetUserPermissionsAsync(guidId);

            return null;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.permissions] Failed to get permissions for user {UserId}", userId);
            return null;
        }
    }

    private static void EnrichClaimsIdentity(ClaimsPrincipal principal, CachedUserPermissions permissions)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Add roles if not present
        foreach (var role in permissions.Roles.Where(r => !principal.HasClaim(ClaimTypes.Role, r)))
            identity.AddClaim(new Claim(ClaimTypes.Role, role));

        // Add permissions if not present
        foreach (var permission in permissions.Permissions
                     .Where(p => !principal.HasClaim(AuthCommonConstants.CustomClaims.Permission, p)))
            identity.AddClaim(new Claim(AuthCommonConstants.CustomClaims.Permission, permission));
    }
}
