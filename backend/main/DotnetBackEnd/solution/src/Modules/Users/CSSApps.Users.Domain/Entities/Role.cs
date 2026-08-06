using CSSApps.Shared.Contracts.DomainCommons.Entities;
using CSSApps.Shared.Contracts.DomainCommons.Primitives;
using CSSApps.Shared.Contracts.Primitives;

namespace CSSApps.Users.Domain.Entities.UserAggregate;

/// <summary>
/// Represents a role that can be assigned to users.
/// This is an aggregate root - roles are managed independently and have their own transactional boundary.
/// Roles are typically seeded or managed by administrators through dedicated endpoints.
/// </summary>
public class Role : AuditableEntity, IAggregateRoot
{
    private readonly List<Permission> _permissions = [];

    private readonly List<User> _users = [];

    protected Role()
    {
    } // EF Core uses to initialize instance and populate with reflection

    private Role(string name, string? description, Guid? id = null) : this()
    {
        Id = id ?? Guid.CreateVersion7();
        Name = name;
        Description = description;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public IReadOnlyCollection<Permission> Permissions => _permissions.AsReadOnly();

    // Navigation property for EF Core - not exposed as business logic
    internal IReadOnlyCollection<User> Users => _users.AsReadOnly();

    public static Result<Role> Create(string name, string? description = null, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ResultError("Role name is required");

        var role = new Role(name, description, id);
        return role;
    }

    public Result AddPermission(Permission permission)
    {
        if (permission is null)
            return new ResultError("Permission cannot be null");

        if (!_permissions.Contains(permission))
        {
            _permissions.Add(permission);
        }

        return Result.Success();
    }

    public Result RemovePermission(Permission permission)
    {
        if (permission is null)
            return new ResultError("Permission cannot be null");

        _permissions.Remove(permission);
        return Result.Success();
    }

    public Result UpdateDescription(string? description)
    {
        Description = description;
        return Result.Success();
    }
}