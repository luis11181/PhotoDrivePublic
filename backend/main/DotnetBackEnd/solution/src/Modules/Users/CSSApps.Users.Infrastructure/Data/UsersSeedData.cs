using CSSApps.Shared.Contracts.Constants;
using RoleConstants = CSSApps.Users.Contracts.Constants.Roles;

namespace CSSApps.Users.Infrastructure.Data;

internal static class UsersSeedData
{
    internal const string SeededBy = "System";
    internal static readonly DateTime SeededAtUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly RoleSeedDefinition[] Roles =
    [
        new(
            Guid.Parse("1b7defa3-89c5-4bd1-887d-5317d2b7ed3e"),
            RoleConstants.Owner,
            "Family library owner"),
        new(
            Guid.Parse("ff9257e3-b3a3-4e08-8537-e0f551568bcb"),
            RoleConstants.Member,
            "Family library member")
    ];

    internal static readonly ServiceAccountSeedDefinition[] ServiceAccounts =
    [
        new(
            ServiceAccountIds.IntegrationServiceAccount,
            "service-integration@cssapps.local",
            "Integration Service",
            "Account",
            [])
    ];

    internal static IEnumerable<object> RoleRows => Roles.Select(role => new
    {
        role.Id,
        role.Name,
        role.Description,
        Status = CommonConstants.EntityStatusType.Active,
        CreatedBy = SeededBy,
        CreatedDate = SeededAtUtc,
        UpdatedBy = SeededBy,
        UpdatedDate = SeededAtUtc,
        DeletedBy = (string?)null,
        DeletedDate = (DateTime?)null
    });

    internal sealed record RoleSeedDefinition(Guid Id, string Name, string Description);

    internal sealed record ServiceAccountSeedDefinition(
        Guid Id,
        string Email,
        string FirstName,
        string? LastName,
        string[] RoleNames);
}
