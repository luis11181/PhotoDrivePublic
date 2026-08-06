using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace CSSApps.Shared.Contracts.Constants;

public static class AuthCommonConstants
{

    public abstract record JwtTokens
    {
        public const string AccessToken = "access_token";
        public const string Refresh = "refresh";
    }

    public abstract class LoginProviders
    {
        public const string Google = "GOOGLE";
        public const string Facebook = "FACEBOOK";
        public const string Password = "PASSWORD";
    }

    public abstract class CustomClaims
    {
        public const string Id = "id";
        public const string Permission = "permission";
        public const string Role = ClaimTypes.Role;
    }
}