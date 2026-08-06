using System.ComponentModel.DataAnnotations;
using CSSApps.Shared.Contracts.DomainCommons.Entities;
using CSSApps.Shared.Contracts.DomainCommons.Primitives;
using CSSApps.Shared.Contracts.Primitives;

namespace CSSApps.Users.Domain.Entities.UserAggregate;

/// <summary>
/// Represents a permission that can be assigned to roles.
/// This is an aggregate root - permissions are managed independently and have their own transactional boundary.
/// Permissions are typically seeded or managed by administrators.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Domain entity name")]
public class Permission : AuditableEntity, IAggregateRoot
{
    private readonly List<Role> _roles = [];

    protected Permission()
    {
    } // EF Core uses to initialize instance and populate with reflection

    private Permission(string name, string? description, Guid? id = null) : this()
    {
        Id = id ?? Guid.CreateVersion7();
        Name = name;
        Description = description;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }

    // Navigation property for EF Core - not exposed as business logic
    internal IReadOnlyCollection<Role> Roles => _roles.AsReadOnly();

    public static Result<Permission> Create(string name, string? description = null, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ResultError("Permission name is required");

        return new Permission(name, description, id);
    }

    public Result UpdateDescription(string? description)
    {
        Description = description;
        return Result.Success();
    }
}