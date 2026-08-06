using System.ComponentModel.DataAnnotations;
using CSSApps.Users.Contracts.Constants;

namespace CSSApps.Users.Contracts.DTOs.Auth;

public sealed class SignInOptionsRes
{
    public bool GoogleEnabled { get; init; }
    public bool PasswordEnabled { get; init; }
    public string? GoogleClientId { get; init; }
}

public sealed class ManagedUserRes
{
    public Guid Id { get; init; }
    public string Email { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
    public string Role { get; init; } = null!;
    public string Status { get; init; } = null!;
    public bool GoogleAllowed { get; init; }
    public bool PasswordAllowed { get; init; }
    public bool GoogleLinked { get; init; }
    public bool HasPassword { get; init; }
    public bool MustChangePassword { get; init; }
    public int ActiveSessionCount { get; init; }
}

public sealed class InviteUserReq
{
    [Required, EmailAddress(ErrorMessage = AuthConstants.EmailValidationError)]
    public string Email { get; init; } = null!;

    [Required, MaxLength(100)]
    public string DisplayName { get; init; } = null!;

    [Required]
    public string Role { get; init; } = null!;

    public bool GoogleAllowed { get; init; } = true;
    public bool PasswordAllowed { get; init; }
}

public sealed class UpdateManagedUserReq
{
    [Required, MaxLength(100)]
    public string DisplayName { get; init; } = null!;

    [Required]
    public string Role { get; init; } = null!;

    [Required]
    public string Status { get; init; } = null!;

    public bool GoogleAllowed { get; init; }
    public bool PasswordAllowed { get; init; }
}

public sealed class IssuedResetCodeRes
{
    public string Code { get; init; } = null!;
    public DateTime? ExpiresAt { get; init; }
}

public sealed class InviteUserRes
{
    public ManagedUserRes User { get; init; } = null!;
    public IssuedResetCodeRes? PasswordSetup { get; init; }
}

public sealed class ResetPasswordReq
{
    [Required]
    public string Code { get; init; } = null!;

    [Required, RegularExpression(AuthConstants.PasswordRegex, ErrorMessage = AuthConstants.PasswordValidationError)]
    public string Password { get; init; } = null!;
}

public sealed class SecurityAuditEventRes
{
    public Guid Id { get; init; }
    public string EventType { get; init; } = null!;
    public Guid? ActorUserId { get; init; }
    public Guid? TargetUserId { get; init; }
    public string Outcome { get; init; } = null!;
    public DateTime OccurredAt { get; init; }
}
