using System.Security.Claims;
using System.Text.Encodings.Web;
using CSSApps.Shared.Contracts.Constants;
using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CSSApps.Api.Authentication.ApiKey;

/// <summary>
/// Authentication handler for service API keys.
/// Validates API key, loads user from database, and creates claims identical to JWT auth.
/// </summary>
public class ServiceApiKeySchemeHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string ApiKeyHeaderName = CommonConstants.Headers.ApiKeyHeaderKey;
    private readonly ConfigurationServiceApiKeyValidator _apiKeyValidator;

    public ServiceApiKeySchemeHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ConfigurationServiceApiKeyValidator apiKeyValidator)
        : base(options, logger, encoder)
    {
        _apiKeyValidator = apiKeyValidator;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeaderValues))
                return AuthenticateResult.NoResult();

            var providedApiKey = apiKeyHeaderValues.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(providedApiKey))
                return AuthenticateResult.NoResult();

            var validationResult = await _apiKeyValidator.ValidateApiKeyAsync(providedApiKey);

            if (validationResult.IsFailure)
            {
                Logger.LogWarning("API key validation failed: {Reason}", validationResult.ErrorValue.Message);
                return AuthenticateResult.Fail(validationResult.ErrorValue.Message);
            }

            var user = validationResult.SuccessValue;

            // Generate claims identical to JWT authentication
            var claims = GenerateClaims(user);

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            Logger.LogDebug("Service API key authentication successful for user {UserId} ({Email})",
                user.Id, user.Email);

            return AuthenticateResult.Success(ticket);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error authenticating service API key");
            return AuthenticateResult.Fail("Authentication failed");
        }
    }

    /// <summary>
    /// Generates claims for the authenticated user.
    ///
    /// Includes both JWT registered claim names and standard ClaimTypes for consistency
    /// across all auth methods (JWT, Cookie, API Key). This ensures User.Identity.Name
    /// and User.Claims work correctly regardless of authentication method.
    /// </summary>
    private static IEnumerable<Claim> GenerateClaims(User user)
    {
        // User ID - JWT standard + ClaimTypes for compatibility
        yield return new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString());
        yield return new Claim(ClaimTypes.NameIdentifier, user.Id.ToString());

        // User Name - both formats for User.Identity.Name compatibility
        yield return new Claim(JwtRegisteredClaimNames.Name, user.Name.FullName);
        yield return new Claim(ClaimTypes.Name, user.Name.FullName);

        // Given name
        yield return new Claim(JwtRegisteredClaimNames.GivenName, user.Name.FullName);

        // Email
        yield return new Claim(JwtRegisteredClaimNames.Email, user.Email);

        //Roles and permissions are added via Claims Transformation on each request

        // Authentication method indicator (different from JWT/Cookie)
        yield return new Claim(ClaimTypes.AuthenticationMethod, "ServiceApiKey");
    }
}
