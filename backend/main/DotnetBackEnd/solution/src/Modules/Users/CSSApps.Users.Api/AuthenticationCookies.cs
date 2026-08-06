using CSSApps.Users.Application.Settings;
using CSSApps.Users.Contracts.DTOs.Auth;
using Microsoft.AspNetCore.Http;

namespace CSSApps.Users.Api;

internal static class AuthenticationCookies
{
    internal const string AccessTokenName = "access_token";
    internal const string RefreshTokenName = "refresh_token";
    internal const string RefreshTokenPath = "/api/v1/users/refresh-token";

    public static void Append(HttpResponse response, AuthRes authentication, JwtSettings settings, bool secure)
    {
        if (!string.IsNullOrWhiteSpace(authentication.RefreshToken))
        {
            response.Cookies.Append(RefreshTokenName, authentication.RefreshToken, CreateOptions(
                secure,
                RefreshTokenPath,
                DateTimeOffset.UtcNow.AddMinutes(settings.RefreshTokenExpirationMinutes)));
        }

        response.Cookies.Append(AccessTokenName, authentication.JwtToken, CreateOptions(
            secure,
            "/",
            DateTimeOffset.UtcNow.AddMinutes(settings.AccessTokenExpirationMinutes)));
    }

    public static void Clear(HttpResponse response, bool secure)
    {
        response.Cookies.Delete(AccessTokenName, CreateDeleteOptions(secure, "/"));
        response.Cookies.Delete(RefreshTokenName, CreateDeleteOptions(secure, RefreshTokenPath));
    }

    private static CookieOptions CreateOptions(bool secure, string path, DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Strict,
        Path = path,
        Expires = expiresAt,
        IsEssential = true
    };

    private static CookieOptions CreateDeleteOptions(bool secure, string path) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Strict,
        Path = path,
        IsEssential = true
    };
}
