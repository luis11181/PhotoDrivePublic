using Asp.Versioning;
using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Utils.ApiHelpers.Filters;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Contracts.DTOs.Auth;
using CSSApps.Users.Contracts.Interfaces.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using RequireCsrfToken = CSSApps.Shared.Utils.ApiHelpers.Filters.RequireAntiforgeryTokenAttribute;

namespace CSSApps.Users.Api.Controllers.v1;


/// <summary>
/// Manages the authenticated member's session, profile, password, and CSRF token lifecycle.
/// </summary>
[ApiController]
[Tags("Users - Auth")]
[ApiVersion("1.0")]
//[ApiVersionNeutral]
[Route("api/v{version:apiVersion}/users")]
[Authorize]
//[EnableRateLimiting("fixed")]
public class UsersAuthController : ControllerBase
{
    private readonly bool _requireHttps;
    private readonly IAntiforgery _antiforgery;

    private readonly JwtSettings _jwtSettings;
    private readonly ILocalAuthenticationServices _localAuthenticationServices;
    private readonly ILogger<UsersAuthController> _logger;

    public UsersAuthController(ILogger<UsersAuthController> logger, IHostEnvironment env, IConfiguration configuration, IAntiforgery antiforgery, IOptions<JwtSettings> jwtSettings,
        ILocalAuthenticationServices localAuthenticationServices)
    {
        _logger = logger;
        _requireHttps = configuration.GetValue<bool?>("Security:RequireHttps") ?? !env.IsDevelopment();
        _antiforgery = antiforgery;
        _jwtSettings = jwtSettings.Value;
        _localAuthenticationServices = localAuthenticationServices;
    }

    /// <summary>Sets or changes the password for the authenticated member.</summary>
    /// <remarks>This CSRF-protected action always targets the session owner. A current password is required only when the account already has a usable password.</remarks>
    [HttpPost("set-password")]
    [RequireCsrfToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<CurrentUserRes>))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiRes))]
    public async Task<IActionResult> SetPassword(SetPasswordReq model)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Unauthorized(ApiRes.Unauthorized());
        }

        var res = await _localAuthenticationServices.SetPassword(userId, model);

        if (res.IsFailure)
        {
            _logger.LogInformation("[users.auth] set password failed userId={UserId} reason={Reason}", userId, res.ErrorValue.ToString());
            return BadRequest(ApiRes.BadRequest(res.ErrorValue.ToString()));
        }

        return Ok(ApiRes<CurrentUserRes>.Success(res.SuccessValue));
    }

    /// <summary>Returns the authenticated member and issues the current session's CSRF token.</summary>
    /// <remarks>Frontend clients must echo the returned <c>X-CSRF-Token</c> on subsequent authenticated mutations.</remarks>
    [HttpGet("me")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<CurrentUserRes>))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ApiRes))]
    public async Task<IActionResult> Me()
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Unauthorized(ApiRes.Unauthorized());
        }

        _logger.LogInformation("[users.session] current user lookup started userId={UserId}", userId);
        var result = await _localAuthenticationServices.GetCurrentUser(userId);
        if (result.IsFailure)
        {
            _logger.LogWarning("[users.session] current user lookup failed userId={UserId}", userId);
            return Unauthorized(ApiRes.Unauthorized());
        }

        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        Response.Headers.Append("X-CSRF-Token", tokens.RequestToken);
        _logger.LogInformation("[users.session] current user lookup completed userId={UserId}", userId);
        return Ok(ApiRes<CurrentUserRes>.Success(result.SuccessValue));
    }

    /// <summary>Ends the current browser session and clears authentication cookies.</summary>
    /// <remarks>This CSRF-protected operation affects only the current session. It does not delete the member or library.</remarks>
    [HttpPost("logout")]
    [RequireCsrfToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes))]
    public async Task<IActionResult> Logout(LogoutReq? model = null)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !Guid.TryParse(User.FindFirstValue("session_id"), out var sessionId))
        {
            AuthenticationCookies.Clear(Response, _requireHttps || Request.IsHttps);
            return Ok(ApiRes.Success());
        }

        var res = await _localAuthenticationServices.Logout(userId, sessionId, model?.LogoutFromAllDevices ?? false);
        AuthenticationCookies.Clear(Response, _requireHttps || Request.IsHttps);

        if (res.IsFailure)
        {
            _logger.LogWarning("[users.auth] logout failed reason={Reason}", res.ErrorValue.ToString());
            // Even if logout fails, we still want to clear the cookie and return success to the client
        }

        return Ok(ApiRes.Success());
    }

    /// <summary>Exchanges a valid refresh token for a renewed authenticated session.</summary>
    /// <remarks>The rotating credential is accepted only from its path-scoped HttpOnly cookie. A valid antiforgery token is required.</remarks>
    [AllowAnonymous]
    [HttpPost("refresh-token")]
    [RequireCsrfToken]
    [EnableRateLimiting("StrictPolicy")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<AuthSessionRes>))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ApiRes))]
    public async Task<IActionResult> RefreshToken()
    {
        var refreshToken = Request.Cookies[AuthenticationCookies.RefreshTokenName];

        if (string.IsNullOrEmpty(refreshToken))
        {
            AuthenticationCookies.Clear(Response, _requireHttps || Request.IsHttps);
            return Unauthorized(ApiRes.Unauthorized());
        }

        var res = await _localAuthenticationServices.RefreshToken(new RefreshTokenReq { RefreshToken = refreshToken });

        if (res.IsFailure)
        {
            _logger.LogInformation("[users.auth] token refresh failed reason={Reason}", res.ErrorValue.ToString());
            AuthenticationCookies.Clear(Response, _requireHttps || Request.IsHttps);
            return Unauthorized(ApiRes.Unauthorized());
        }

        AuthenticationCookies.Append(Response, res.SuccessValue, _jwtSettings, _requireHttps || Request.IsHttps);
        return Ok(ApiRes<AuthSessionRes>.Success(new AuthSessionRes
        {
            User = res.SuccessValue.User,
            ExpiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes)
        }));
    }

    /// <summary>Issues an anonymous antiforgery token used to authorize cookie-based session refresh.</summary>
    [AllowAnonymous]
    [HttpGet("csrf-token")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult CsrfToken()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        Response.Headers.Append("X-CSRF-Token", tokens.RequestToken);
        return NoContent();
    }
}
