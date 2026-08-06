using CSSApps.Shared.Contracts.DomainCommons.Entities;
using CSSApps.Shared.Contracts.DomainCommons.Primitives;
using CSSApps.Shared.Contracts.Primitives;

namespace CSSApps.Users.Domain.Entities.UserAggregate;

public sealed class SecurityToken : AuditableEntity, IAggregateRoot
{
    private SecurityToken()
    {
    }

    private SecurityToken(
        Guid userId,
        SecurityTokenPurpose purpose,
        string secretHash,
        DateTime? expiresAt,
        Guid? createdByUserId)
    {
        Id = Guid.CreateVersion7();
        UserId = userId;
        Purpose = purpose;
        SecretHash = secretHash;
        ExpiresAt = expiresAt;
        CreatedByUserId = createdByUserId;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public SecurityTokenPurpose Purpose { get; private set; }
    public string SecretHash { get; private set; } = null!;
    public DateTime? ExpiresAt { get; private set; }
    public DateTime? UsedAt { get; private set; }
    public Guid? CreatedByUserId { get; private set; }

    public bool CanUse(DateTime now) => UsedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public static Result<SecurityToken> Create(
        Guid userId,
        SecurityTokenPurpose purpose,
        string secretHash,
        DateTime? expiresAt,
        Guid? createdByUserId = null)
    {
        if (userId == Guid.Empty) return new ResultError("User ID is required");
        if (string.IsNullOrWhiteSpace(secretHash)) return new ResultError("Token hash is required");
        if (expiresAt is not null && expiresAt <= DateTime.UtcNow)
            return new ResultError("Token expiry must be in the future");

        return new SecurityToken(userId, purpose, secretHash, expiresAt, createdByUserId);
    }

    public Result Consume(DateTime now)
    {
        if (!CanUse(now)) return new ResultError("Token is invalid or expired");
        UsedAt = now;
        return Result.Success();
    }
}
