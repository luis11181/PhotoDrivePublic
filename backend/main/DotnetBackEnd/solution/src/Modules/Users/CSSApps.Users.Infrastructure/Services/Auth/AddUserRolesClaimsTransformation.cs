using System.Security.Claims;
using CSSApps.Shared.Utils.Common.Auth;
using CSSApps.Users.Contracts.Interfaces.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSSApps.Users.Infrastructure.Services.Auth;

/// <summary>
/// Adds the user's role claims (Owner/Member), which the "Owner" policy requires. Login tokens do not carry roles,
/// so the authentication middleware calls this on every authenticated request; the lookup is cached briefly and
/// cleared when an Owner changes someone's role.
/// </summary>
public class AddUserRolesClaimsTransformation(
    IServiceProvider serviceProvider,
    ILogger<AddUserRolesClaimsTransformation> logger) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        try
        {
            if (!principal.Identity?.IsAuthenticated ?? true) return principal;
            if (principal.HasClaim(claim => claim.Type == ClaimTypes.Role)) return principal;
            if (!Guid.TryParse(AuthUtils.GetUserIdentifier(principal), out var userId))
            {
                logger.LogWarning("[users.roles] No user identifier found in claims");
                return principal;
            }

            // Resolve from the request scope: it already carries the request's database route (local replica for
            // reads), whereas a new scope would open an extra connection to the cloud database on every request.
            var roles = await serviceProvider.GetRequiredService<IUserRolesService>().GetUserRolesAsync(userId);
            if (roles is null) return principal;

            var identity = (ClaimsIdentity)principal.Identity!;
            foreach (var role in roles) identity.AddClaim(new Claim(ClaimTypes.Role, role));
            return principal;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[users.roles] Claims transformation failed");
            return principal;
        }
    }
}
