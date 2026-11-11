using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CSSApps.Shared.Contracts.Constants;
using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

namespace CSSApps.Users.Application.Services.Auth.JwtAuth;

/// <summary>
/// Service responsible for JWT token generation and validation.
/// 
/// Handles two types of tokens:
/// - Access Token: Short-lived JWT for API authentication (always generated)
/// - Refresh Token: Long-lived, opaque, rotating credential stored only as a hash
/// </summary>
internal sealed class JwtAuth
{
    private readonly JwtSettings _jwtSettings;
    private readonly ILogger<JwtAuth> _logger;
    private readonly IUsersDbContext _dbContext;

    public JwtAuth(
        IOptions<JwtSettings> jwtSettings,
        ILogger<JwtAuth> logger,
        IUsersDbContext dbContext)
    {
        _jwtSettings = jwtSettings.Value;
        _logger = logger;
        _dbContext = dbContext;
    }

    #region Access Token (Primary Authentication)

    /// <summary>
    /// Generates an access token (JWT) for the user.
    /// This is the primary authentication token stored in HttpOnly cookie.
    /// </summary>
    public Task<Result<string>> GenerateAccessTokenAsync(User user, UserSession session)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler
            {
                SetDefaultTimesOnTokenCreation = true
            };

            var key = Encoding.UTF8.GetBytes(_jwtSettings.AccessTokenSecret);
            var securityKey = new SymmetricSecurityKey(key);
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                IssuedAt = DateTime.UtcNow,
                Issuer = _jwtSettings.Issuer,
                Audience = _jwtSettings.Audience,
                Expires = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
                SigningCredentials = credentials,
                Subject = GenerateClaims(user, session),
                TokenType = "JWT"
            };

            var token = handler.CreateToken(tokenDescriptor);
            var tokenString = handler.WriteToken(token);

            _logger.LogDebug("[users.token] Generated access token for user {UserId}", user.Id);
            return Task.FromResult(Result<string>.Success(tokenString));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.token] Failed to generate access token for user {UserId}", user.Id);
            return Task.FromResult(Result<string>.Failure(new ResultError($"Failed to generate access token: {ex.Message}")));
        }
    }

    /// <summary>
    /// Generates claims for the access token.
    /// 
    /// Includes both JWT registered claim names (for token compatibility) and 
    /// standard ClaimTypes (for ClaimsIdentity compatibility).
    /// This ensures User.Identity.Name and User.Claims work correctly across
    /// all authentication methods (JWT, Cookie, API Key).
    /// 
    /// Claims included:
    /// - sub/NameIdentifier: User ID
    /// - unique_name/Name: User's full name (for User.Identity.Name)
    /// - email/Email: User's email address
    /// - given_name/GivenName: User's full name
    /// - Custom "id" claim: User ID
    /// </summary>
    private static ClaimsIdentity GenerateClaims(User user, UserSession session)
    {
        var ci = new ClaimsIdentity();
        
        // User ID - JWT standard + ClaimTypes for compatibility
        ci.AddClaim(new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()));
        ci.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        
        // User Name - both formats for User.Identity.Name compatibility
        // Note: JwtSecurityTokenHandler maps ClaimTypes.Name to "unique_name" in JWT
        ci.AddClaim(new Claim(JwtRegisteredClaimNames.Name, user.Name.FullName));
        ci.AddClaim(new Claim(ClaimTypes.Name, user.Name.FullName));
        
        // Given name
        ci.AddClaim(new Claim(JwtRegisteredClaimNames.GivenName, user.Name.FullName));
        
        // Email
        ci.AddClaim(new Claim(JwtRegisteredClaimNames.Email, user.Email));
        ci.AddClaim(new Claim("security_stamp", user.SecurityStamp.ToString("N")));
        ci.AddClaim(new Claim("session_id", session.Id.ToString()));
        if (user.MustChangePassword) ci.AddClaim(new Claim("must_change_password", "true"));

        // Roles are added by AddUserRolesClaimsTransformation on each request
        // foreach (var role in user.Roles)
        // {
        //     ci.AddClaim(new Claim("role", role.Name));
        //     ci.AddClaim(new Claim(ClaimTypes.Role, role.Name));
        // }

        return ci;
    }

    #endregion

    #region Refresh Token

    /// <summary>
    /// Generates a cryptographically random refresh credential for the user session.
    /// Only its SHA-256 hash is persisted.
    /// </summary>
    public Task<Result<string>> GenerateRefreshTokenAsync(User user, UserSession session, string? provider = null)
    {
        try
        {
            var randomValue = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));
            var tokenString = $"v1.{user.SecurityStamp:N}.{randomValue}";

            // Store token in database for tracking and revocation
            var userTokenResult = UserToken.CreateRefreshToken(
                user.Id,
                HashToken(tokenString),
                DateTime.UtcNow.AddMinutes(_jwtSettings.RefreshTokenExpirationMinutes),
                provider,
                session.Id,
                jti: null);

            if (userTokenResult.IsFailure)
                return Task.FromResult(Result<string>.Failure(userTokenResult.ErrorValue));

            var userToken = userTokenResult.SuccessValue;

            _dbContext.UserTokens.Add(userToken);

            _logger.LogInformation("[users.token] Generated refresh token for user {UserId} session {SessionId}",
                user.Id, session.Id);

            return Task.FromResult(Result<string>.Success(tokenString));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.token] Failed to generate refresh token for user {UserId}", user.Id);
            return Task.FromResult(Result<string>.Failure(new ResultError("Failed to generate refresh token")));
        }
    }

    /// <summary>
    /// Resolves a refresh credential to its persisted token, user, and session.
    /// Revocation and expiration are returned to the caller so replay can revoke the token family.
    /// </summary>
    public async Task<Result<ValidatedRefreshToken>> ValidateRefreshTokenAsync(string refreshToken)
    {
        try
        {
            if (!TryReadSecurityStamp(refreshToken, out var issuedSecurityStamp))
                return new ResultError("Invalid refresh token");

            var tokenHash = HashToken(refreshToken);
            var userToken = await _dbContext.UserTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t =>
                    t.TokenHash == tokenHash &&
                    t.TokenType == AuthCommonConstants.JwtTokens.Refresh);

            if (userToken == null)
            {
                _logger.LogInformation("[users.token] Refresh credential was not recognized");
                return new ResultError("Invalid refresh token");
            }

            var user = await _dbContext.Users
                .Include(candidate => candidate.Roles)
                .FirstOrDefaultAsync(candidate => candidate.Id == userToken.UserId);
            if (user == null || user.IsServiceAccount ||
                user.AccountStatus == HumanAccountStatus.Disabled ||
                user.SecurityStamp != issuedSecurityStamp)
            {
                _logger.LogInformation("[users.token] Refresh credential no longer matches an active account");
                return new ResultError("Invalid refresh token");
            }

            if (!userToken.SessionId.HasValue)
                return new ResultError("Invalid refresh token");
            var session = await _dbContext.UserSessions.FindAsync(userToken.SessionId.Value);
            if (session == null || session.UserId != user.Id)
            {
                _logger.LogInformation("[users.token] Refresh credential session was not recognized");
                return new ResultError("Invalid refresh token");
            }

            _logger.LogDebug("[users.token] Resolved refresh credential for user {UserId} session {SessionId}",
                user.Id, session.Id);

            return new ValidatedRefreshToken(user, session, userToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.token] Failed to validate refresh token");
            return new ResultError("Failed to validate refresh token");
        }
    }

    public async Task<bool> TryConsumeRefreshTokenAsync(Guid tokenId)
    {
        var now = DateTime.UtcNow;
        var updated = await _dbContext.UserTokens
            .Where(token => token.Id == tokenId && !token.IsRevoked && token.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.IsRevoked, true));
        return updated == 1;
    }

    public async Task RevokeRefreshTokenFamilyForReplayAsync(Guid sessionId)
    {
        await RevokeSessionRefreshTokensAsync(sessionId, "Refresh token replay detected");
        var session = await _dbContext.UserSessions.FindAsync(sessionId);
        if (session?.IsActive == true) session.Terminate("Refresh token replay detected");
    }

    #endregion

    #region Token Revocation

    public async Task<Result> RevokeRefreshTokenAsync(string refreshToken, string? reason = null)
    {
        try
        {
            var tokenHash = HashToken(refreshToken);
            var userToken = await _dbContext.UserTokens
                .FirstOrDefaultAsync(t =>
                    t.TokenHash == tokenHash &&
                    t.TokenType == AuthCommonConstants.JwtTokens.Refresh);

            if (userToken == null)
                return new ResultError("Refresh token not found");

            if (!userToken.IsRevoked) userToken.Revoke();

            _logger.LogInformation("[users.token] Revoked refresh token for user {UserId}. Reason: {Reason}",
                userToken.UserId, reason ?? "Not specified");

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.token] Failed to revoke refresh token");
            return new ResultError($"Failed to revoke refresh token: {ex.Message}");
        }
    }

    public async Task<Result> RevokeAllUserRefreshTokensAsync(Guid userId, string? reason = null)
    {
        try
        {
            var userTokens = await _dbContext.UserTokens
                .Where(t => t.UserId == userId &&
                           t.TokenType == AuthCommonConstants.JwtTokens.Refresh &&
                           !t.IsRevoked)
                .ToListAsync();

            foreach (var token in userTokens)
            {
                token.Revoke();
            }

            _logger.LogInformation("[users.token] Revoked {Count} refresh tokens for user {UserId}. Reason: {Reason}",
                userTokens.Count, userId, reason ?? "Not specified");

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.token] Failed to revoke all refresh tokens for user {UserId}", userId);
            return new ResultError($"Failed to revoke refresh tokens: {ex.Message}");
        }
    }

    public async Task<Result> RevokeSessionRefreshTokensAsync(Guid sessionId, string? reason = null)
    {
        try
        {
            var userTokens = await _dbContext.UserTokens
                .Where(t => t.SessionId == sessionId &&
                           t.TokenType == AuthCommonConstants.JwtTokens.Refresh &&
                           !t.IsRevoked)
                .ToListAsync();

            foreach (var token in userTokens)
            {
                token.Revoke();
            }

            _logger.LogInformation("[users.token] Revoked {Count} refresh tokens for session {SessionId}. Reason: {Reason}",
                userTokens.Count, sessionId, reason ?? "Not specified");

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.token] Failed to revoke session refresh tokens for session {SessionId}", sessionId);
            return new ResultError($"Failed to revoke session tokens: {ex.Message}");
        }
    }

    #endregion

    #region Token Maintenance

    public async Task<Result> CleanupExpiredTokensAsync()
    {
        try
        {
            // Keep consumed rows for one additional refresh lifetime. A token rotated near its
            // expiry must remain recognizable for as long as its replacement can still be active.
            var replayRetentionCutoff = DateTime.UtcNow.AddMinutes(-_jwtSettings.RefreshTokenExpirationMinutes);
            var expiredTokens = await _dbContext.UserTokens
                .Where(t => t.ExpiresAt <= replayRetentionCutoff)
                .ToListAsync();

            _dbContext.UserTokens.RemoveRange(expiredTokens);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("[users.token] Cleaned up {Count} expired/revoked tokens", expiredTokens.Count);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.token] Failed to cleanup expired tokens");
            return new ResultError($"Failed to cleanup tokens: {ex.Message}");
        }
    }

    #endregion

    #region Private Helpers

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static bool TryReadSecurityStamp(string token, out Guid securityStamp)
    {
        securityStamp = Guid.Empty;
        var parts = token.Split('.', 3, StringSplitOptions.None);
        return parts.Length == 3 && parts[0] == "v1" &&
               Guid.TryParseExact(parts[1], "N", out securityStamp) &&
               parts[2].Length >= 80;
    }

    #endregion
}

internal sealed record ValidatedRefreshToken(User User, UserSession Session, UserToken Token);
