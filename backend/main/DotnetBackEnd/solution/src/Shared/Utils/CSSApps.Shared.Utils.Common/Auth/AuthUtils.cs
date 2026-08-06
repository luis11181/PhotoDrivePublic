using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CSSApps.Shared.Contracts.Constants;

namespace CSSApps.Shared.Utils.Common.Auth;

public static class AuthUtils
{
    public static string? GetUserIdentifier(ClaimsPrincipal principal)
    {
        // Try multiple claim types for user ID
        var userIdClaims = new[]
        {
            ClaimTypes.NameIdentifier,
            JwtRegisteredClaimNames.Sub,
            AuthCommonConstants.CustomClaims.Id
        };

        return userIdClaims
            .Select(claimType => principal.FindFirstValue(claimType))
            .FirstOrDefault(id => !string.IsNullOrEmpty(id));
    }

    public static string GetUserDisplayName(ClaimsPrincipal? principal, string fallback = "current authenticated user")
    {
        if (principal is null)
        {
            return fallback;
        }

        var candidates = new[]
        {
            principal.FindFirstValue(ClaimTypes.Name),
            principal.FindFirstValue(ClaimTypes.GivenName),
            principal.FindFirstValue(JwtRegisteredClaimNames.Name),
            principal.FindFirstValue(JwtRegisteredClaimNames.Email),
            principal.Identity?.Name
        };

        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? fallback;
    }

    public static string? GetUserEmail(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        var candidates = new[]
        {
            principal.FindFirstValue(ClaimTypes.Email),
            principal.FindFirstValue(JwtRegisteredClaimNames.Email),
            principal.FindFirstValue("email")
        };

        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
