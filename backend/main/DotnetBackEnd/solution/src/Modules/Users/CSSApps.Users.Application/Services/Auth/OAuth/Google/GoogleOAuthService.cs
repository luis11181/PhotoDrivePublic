using System.Net.Http.Headers;
using System.Text.Json;
using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Domain.Entities.UserAggregate;
using CSSApps.Users.Domain.Entities.UserAggregate.ValueObjects;
using CSSApps.Users.Domain.Errors;
using Google.Apis.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSSApps.Users.Application.Services.Auth.OAuth.Google;

internal sealed class GoogleOAuthService
{
    private readonly GoogleOAuthConfig _config;
    private readonly ILogger<GoogleOAuthService> _logger;
    private readonly IUsersDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public GoogleOAuthService(
        IOptions<GoogleOAuthConfig> config,
        ILogger<GoogleOAuthService> logger,
        IUsersDbContext dbContext,
        HttpClient httpClient)
    {
        _config = config.Value;
        _logger = logger;
        _dbContext = dbContext;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Verifies Google's signature and configured audience, and requires Google to assert that
    /// the email is verified before it can participate in the initial allowlist match.
    /// </summary>
    public async Task<Result<GoogleJsonWebSignature.Payload>> ValidateIdTokenAsync(string idToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(idToken))
            {
                _logger.LogWarning("[users.oauth] Empty or null ID token provided for validation");
                return AuthErrors.InvalidGoogleToken;
            }
            if (!_config.IsConfigured) return AuthErrors.OAuthAuthenticationFailed;

            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _config.ClientId }
            });

            if (payload == null || string.IsNullOrEmpty(payload.Email) || !payload.EmailVerified)
            {
                _logger.LogWarning("[users.oauth] Invalid Google token - payload is null or email is missing");
                return AuthErrors.InvalidGoogleToken;
            }

            _logger.LogDebug("[users.oauth] Successfully validated Google ID token");
            return payload;
        }
        catch (InvalidJwtException ex)
        {
            _logger.LogWarning(ex, "[users.oauth] Invalid Google JWT token");
            return AuthErrors.InvalidGoogleToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.oauth] Error validating Google ID token");
            return new ResultError("Failed to validate Google token");
        }
    }

    public async Task<Result<GoogleUserProfile>> GetUserProfileAsync(string accessToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogWarning("[users.oauth] Empty or null access token provided for user profile retrieval");
                return new ResultError("Access token is required");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_config.UserInfoUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[users.oauth] Failed to fetch Google user profile. Status: {StatusCode}, Reason: {ReasonPhrase}",
                    response.StatusCode, response.ReasonPhrase);
                return new ResultError("Failed to fetch user profile from Google");
            }

            var content = await response.Content.ReadAsStringAsync();
            
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("[users.oauth] Empty response received from Google UserInfo API");
                return new ResultError("Empty response from Google");
            }

            var userProfile = JsonSerializer.Deserialize<GoogleUserProfile>(content, _jsonOptions);

            if (userProfile == null)
            {
                _logger.LogWarning("[users.oauth] Failed to deserialize Google user profile response");
                return new ResultError("Invalid response format from Google");
            }

            _logger.LogDebug("[users.oauth] Successfully retrieved Google user profile");
            return userProfile;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[users.oauth] HTTP error while fetching Google user profile");
            return new ResultError("Network error while fetching user profile");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[users.oauth] JSON parsing error while processing Google user profile");
            return new ResultError("Invalid response format from Google");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.oauth] Unexpected error while fetching Google user profile");
            return new ResultError("Failed to fetch user profile");
        }
    }

    public async Task<Result<OAuthAuthenticatedUser>> AuthenticateAsync(string idToken, string? accessToken = null)
    {
        try
        {
            // Validate the ID token first
            var payloadResult = await ValidateIdTokenAsync(idToken);
            if (payloadResult.IsFailure)
                return payloadResult.ErrorValue;

            var payload = payloadResult.SuccessValue;

            // Try to get additional profile information if access token is provided
            GoogleUserProfile? userProfile = null;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var profileResult = await GetUserProfileAsync(accessToken);
                if (profileResult.IsSuccess)
                {
                    userProfile = profileResult.SuccessValue;
                }
                else
                {
                    _logger.LogWarning("[users.oauth] Failed to get extended user profile, using basic info from ID token");
                }
            }

            // Use profile data if available, otherwise fall back to payload data
            var email = userProfile?.Email ?? payload.Email;
            var firstName = userProfile?.GivenName ?? payload.GivenName ?? payload.Name?.Split(' ').FirstOrDefault() ?? "User";
            var lastName = userProfile?.FamilyName ?? payload.FamilyName ?? payload.Name?.Split(' ').Skip(1).FirstOrDefault() ?? "";

            return await FindOrCreateOAuthUser(payload.Subject, email, firstName, lastName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.oauth] Error during Google authentication");
            return AuthErrors.OAuthAuthenticationFailed;
        }
    }

    /// <summary>
    /// Uses Google's stable subject for every login after the first binding. Email is consulted
    /// only once to bind an already-allowlisted account; an unknown email never creates a user.
    /// </summary>
    private async Task<Result<OAuthAuthenticatedUser>> FindOrCreateOAuthUser(
        string providerId,
        string email,
        string firstName,
        string lastName)
    {
        try
        {
            // The provider subject is the durable identity once an account has been linked.
            var existingOAuthUser = await _dbContext.Users
                .Include(user => user.Roles)
                .FirstOrDefaultAsync(u => u.OAuthProvider == "Google" && u.OAuthProviderId == providerId);

            if (existingOAuthUser != null)
            {
                if (!existingOAuthUser.CanUse(LoginMethod.Google))
                    return AuthErrors.OAuthAuthenticationFailed;
                existingOAuthUser.Activate();
                _logger.LogDebug("[users.oauth] Found existing Google user: {UserId}", existingOAuthUser.Id);
                return new OAuthAuthenticatedUser(existingOAuthUser, false);
            }

            // A verified email may bind Google only to a record the owner already allowlisted.
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var existingEmailUser = await _dbContext.Users
                .Include(user => user.Roles)
                .FirstOrDefaultAsync(u => u.Email == normalizedEmail);

            if (existingEmailUser != null)
            {
                if (!existingEmailUser.CanUse(LoginMethod.Google))
                    return AuthErrors.OAuthAuthenticationFailed;
                var linkResult = existingEmailUser.LinkOAuthProvider("Google", providerId);
                if (linkResult.IsFailure)
                {
                    _logger.LogWarning("[users.oauth] Failed to link Google account for user {UserId}: {Error}",
                        existingEmailUser.Id, linkResult.ErrorValue.Message);
                    return linkResult.ErrorValue;
                }

                // Mark email as verified since Google has verified it
                existingEmailUser.VerifyEmail();
                existingEmailUser.Activate();

                _logger.LogInformation("[users.oauth] Linked Google account to existing user {UserId}",
                    existingEmailUser.Id);

                return new OAuthAuthenticatedUser(existingEmailUser, false);
            }

            _logger.LogInformation("[users.oauth] Rejected Google account because it is not allowlisted");
            return AuthErrors.OAuthAuthenticationFailed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.oauth] Error finding or creating Google OAuth user");
            return new ResultError("Failed to process user account");
        }
    }
}
