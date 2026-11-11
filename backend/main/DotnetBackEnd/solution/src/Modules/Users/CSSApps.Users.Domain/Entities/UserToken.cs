using CSSApps.Shared.Contracts.Constants;
using CSSApps.Shared.Contracts.DomainCommons.Entities;
using CSSApps.Shared.Contracts.DomainCommons.Primitives;
using CSSApps.Shared.Contracts.Primitives;

namespace CSSApps.Users.Domain.Entities.UserAggregate;

/// <summary>
/// Represents a user authentication token (access or refresh).
/// This is an aggregate root - tokens are managed independently by JwtAuth service
/// and have their own transactional boundary.
/// </summary>
public class UserToken : AuditableEntity, IAggregateRoot
{
    protected UserToken() { } // EF Core constructor

    private UserToken(Guid userId, string tokenType, string tokenHash, DateTime expiresAt,
        string? provider = null, Guid? sessionId = null, string? jti = null)
    {
        Id = Guid.CreateVersion7();
        UserId = userId;
        TokenType = tokenType;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        Provider = provider;
        SessionId = sessionId;
        Jti = jti;
        IsRevoked = false;
    }

    public Guid Id { get; private set; }
    
    public Guid UserId { get; private set; }
    
    /// <summary>
    /// Session ID for tracking which session this token belongs to
    /// </summary>
    public Guid? SessionId { get; private set; }
    
    public string TokenType { get; private set; } = null!; // access_token, refresh_token, etc
    
    public string TokenHash { get; private set; } = null!;
    
    /// <summary>
    /// Optional external token identifier retained for non-browser token types.
    /// </summary>
    public string? Jti { get; private set; }
    
    public DateTime ExpiresAt { get; private set; }
    
    public string? Provider { get; private set; } // Google, Facebook, etc (null for local tokens)
    
    public bool IsRevoked { get; private set; }

    // Navigation property for EF Core
    internal UserSession? Session { get; private set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    public bool IsValid => !IsRevoked && !IsExpired;

    public static Result<UserToken> CreateAccessToken(Guid userId, string tokenHash, DateTime expiresAt,
        string? provider = null, Guid? sessionId = null, string? jti = null)
    {
        if (userId == Guid.Empty)
            return new ResultError("User ID cannot be empty");

        if (string.IsNullOrWhiteSpace(tokenHash))
            return new ResultError("Token hash cannot be empty");

        if (expiresAt <= DateTime.UtcNow)
            return new ResultError("Expiration date must be in the future");

        return new UserToken(userId, AuthCommonConstants.JwtTokens.AccessToken, tokenHash, expiresAt, provider, sessionId, jti);
    }

    public static Result<UserToken> CreateRefreshToken(Guid userId, string tokenHash, DateTime expiresAt,
        string? provider = null, Guid? sessionId = null, string? jti = null)
    {
        if (userId == Guid.Empty)
            return new ResultError("User ID cannot be empty");

        if (string.IsNullOrWhiteSpace(tokenHash))
            return new ResultError("Token hash cannot be empty");

        if (expiresAt <= DateTime.UtcNow)
            return new ResultError("Expiration date must be in the future");

        return new UserToken(userId, AuthCommonConstants.JwtTokens.Refresh, tokenHash, expiresAt, provider, sessionId, jti);
    }

    public Result Revoke()
    {
        if (IsRevoked)
            return new ResultError("Token is already revoked");

        IsRevoked = true;
        return Result.Success();
    }

    public Result UpdateExpiry(DateTime newExpiresAt)
    {
        if (newExpiresAt <= DateTime.UtcNow)
            return new ResultError("New expiration date must be in the future");

        ExpiresAt = newExpiresAt;
        return Result.Success();
    }
}
