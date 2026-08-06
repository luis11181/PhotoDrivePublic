using CSSApps.Shared.Contracts.Constants;
using PermissionConstants = CSSApps.Users.Contracts.Constants.Permissions;
using RoleConstants = CSSApps.Users.Contracts.Constants.Roles;

namespace CSSApps.Users.Infrastructure.Data;

internal static class UsersSeedData
{
    internal const string SeededBy = "System";
    internal static readonly DateTime SeededAtUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly PermissionSeedDefinition[] Permissions =
    [
        new(
            Guid.Parse("7ca69cf0-d5b1-4db0-93e8-62714da2cd09"),
            PermissionConstants.ReadUsers,
            "Can read user data"),
        new(
            Guid.Parse("e0581e2b-c663-4da2-8005-6600627fd589"),
            PermissionConstants.WriteUsers,
            "Can modify user data"),
        new(
            Guid.Parse("9cb8d685-a9bf-4c78-9ec2-f3790428c35d"),
            PermissionConstants.ManageRoles,
            "Can manage roles")
    ];

    internal static readonly RoleSeedDefinition[] Roles =
    [
        new(
            Guid.Parse("1b7defa3-89c5-4bd1-887d-5317d2b7ed3e"),
            RoleConstants.Owner,
            "Family library owner",
            [PermissionConstants.ReadUsers, PermissionConstants.WriteUsers, PermissionConstants.ManageRoles]),
        new(
            Guid.Parse("ff9257e3-b3a3-4e08-8537-e0f551568bcb"),
            RoleConstants.Member,
            "Family library member",
            [])
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

    internal static IEnumerable<object> PermissionRows => Permissions.Select(permission => new
    {
        permission.Id,
        permission.Name,
        permission.Description,
        Status = CommonConstants.EntityStatusType.Active,
        CreatedBy = SeededBy,
        CreatedDate = SeededAtUtc,
        UpdatedBy = SeededBy,
        UpdatedDate = SeededAtUtc,
        DeletedBy = (string?)null,
        DeletedDate = (DateTime?)null
    });

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

    internal static IEnumerable<object> RolePermissionRows =>
        from role in Roles
        from permissionName in role.PermissionNames
        join permission in Permissions on permissionName equals permission.Name
        select new
        {
            permission_id = permission.Id,
            role_id = role.Id
        };

    internal sealed record PermissionSeedDefinition(Guid Id, string Name, string Description);

    internal sealed record RoleSeedDefinition(Guid Id, string Name, string Description, string[] PermissionNames);

    internal sealed record ServiceAccountSeedDefinition(
        Guid Id,
        string Email,
        string FirstName,
        string? LastName,
        string[] RoleNames);
}
