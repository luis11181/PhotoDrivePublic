using Asp.Versioning;
using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Utils.ApiHelpers;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Contracts.DTOs.Auth;
using CSSApps.Users.Contracts.Interfaces.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;


namespace CSSApps.Users.Api.Controllers.v1;

/// <summary>
/// Handles anonymous sign-in methods and establishes the authenticated browser session.
/// </summary>
[ApiController]
[Tags("Users - Auth/Login")]
[ApiVersion("1.0")]
//[ApiVersionNeutral]
[Route("api/v{version:apiVersion}/users")]
[Authorize]
//[EnableRateLimiting("fixed")]
public class UsersAuthLoginController : ControllerBase
{
    private readonly bool _requireHttps;
    private readonly IAntiforgery _antiforgery;

    private readonly JwtSettings _jwtSettings;
    private readonly ILocalAuthenticationServices _localAuthenticationServices;
    private readonly ILogger<UsersAuthLoginController> _logger;

    public UsersAuthLoginController(ILogger<UsersAuthLoginController> logger, IHostEnvironment env, IConfiguration configuration, IAntiforgery antiforgery, IOptions<JwtSettings> jwtSettings,
        ILocalAuthenticationServices localAuthenticationServices)
    {
        _logger = logger;
        _requireHttps = configuration.GetValue<bool?>("Security:RequireHttps") ?? !env.IsDevelopment();
        _antiforgery = antiforgery;
        _jwtSettings = jwtSettings.Value;
        _localAuthenticationServices = localAuthenticationServices;
    }


    /// <summary>Signs in an existing member with email and password.</summary>
    /// <remarks>On success, the API establishes secure browser cookies and returns session metadata. Passwords are never returned or stored in browser-visible responses.</remarks>
    //[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme + "," + ApiKeySchemeOptions.DefaultScheme)]
    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting("StrictPolicy")]
    // [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(APIResponse<AuthenticateResponse>))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ApiRes))]
    public async Task<IActionResult> Login(LoginReq model)
    {
        _logger.LogInformation("[users.auth] password sign-in started");
        var ipAddress = RequestInfo.GetClientIpAddress(HttpContext);
        var userAgent = Request.Headers.UserAgent.ToString() ?? "Unknown";

        var res = await _localAuthenticationServices.Login(model, ipAddress, userAgent);

        if (res.IsFailure)
        {
            _logger.LogInformation("[users.auth] password sign-in failed reason={Reason}", res.ErrorValue.ToString());
            return Unauthorized(ApiRes.Unauthorized(res.ErrorValue.ToString()));
        }

        AppendAuthenticationCookies(res.SuccessValue);

        AppendCsrfToken();
        _logger.LogInformation("[users.auth] password sign-in completed userId={UserId}", res.SuccessValue.Id);
        return Ok(ApiRes<AuthSessionRes>.Success(ToSession(res.SuccessValue)));
    }

    /// <summary>Signs in or creates a member from a verified Google identity credential.</summary>
    /// <remarks>The backend validates the credential against the configured OAuth client before it creates browser-session cookies.</remarks>
    [AllowAnonymous]
    [HttpPost("google-login")]
    [EnableRateLimiting("StrictPolicy")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<AuthSessionRes>))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ApiRes))]
    public async Task<IActionResult> GoogleLogin(GoogleLoginReq model)
    {
        _logger.LogInformation("[users.auth] google sign-in started");
        var result = await _localAuthenticationServices.GoogleLoginOrRegister(
            model.Credential,
            RequestInfo.GetClientIpAddress(HttpContext),
            Request.Headers.UserAgent.ToString() ?? "Unknown");

        if (result.IsFailure)
        {
            _logger.LogInformation("[users.auth] google sign-in failed reason={Reason}", result.ErrorValue);
            return Unauthorized(ApiRes.Unauthorized("Could not sign in with Google"));
        }

        AppendAuthenticationCookies(result.SuccessValue);
        AppendCsrfToken();
        _logger.LogInformation("[users.auth] google sign-in completed userId={UserId}", result.SuccessValue.Id);
        return Ok(ApiRes<AuthSessionRes>.Success(ToSession(result.SuccessValue)));
    }

    private void AppendAuthenticationCookies(AuthRes authentication)
    {
        AuthenticationCookies.Append(Response, authentication, _jwtSettings, _requireHttps || Request.IsHttps);
    }

    private AuthSessionRes ToSession(AuthRes authentication) => new()
    {
        User = authentication.User,
        ExpiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes)
    };

    private void AppendCsrfToken()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        Response.Headers.Append("X-CSRF-Token", tokens.RequestToken);
    }

}
