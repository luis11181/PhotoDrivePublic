using System.ComponentModel.DataAnnotations;

namespace CSSApps.Users.Application.Settings;

public sealed class FamilyAuthenticationOptions
{
    public const string SectionName = "FamilyAuthentication";

    public bool PasswordLoginEnabled { get; init; }

    [Range(5, 1440)]
    public int PasswordResetLifetimeMinutes { get; init; } = 30;

    public BootstrapOwnerOptions BootstrapOwner { get; init; } = new();
}

public sealed class BootstrapOwnerOptions
{
    [EmailAddress]
    public string? Email { get; init; }

    public string? InitialPassword { get; init; }
}
