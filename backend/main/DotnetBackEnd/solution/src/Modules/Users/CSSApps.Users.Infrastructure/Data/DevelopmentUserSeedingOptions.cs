using CSSApps.Users.Contracts.Constants;

namespace CSSApps.Users.Infrastructure.Data;

public sealed class DevelopmentUserSeedingOptions
{
    public const string SectionName = "UserSeeding:DevelopmentDefaultUser";

    public bool Enabled { get; init; }
    public string? Email { get; init; }
    public string? Password { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string Role { get; init; } = Roles.Owner;
}
