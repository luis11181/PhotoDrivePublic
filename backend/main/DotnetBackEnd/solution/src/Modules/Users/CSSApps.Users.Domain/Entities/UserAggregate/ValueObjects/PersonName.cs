namespace CSSApps.Users.Domain.Entities.UserAggregate.ValueObjects;

/// <summary>
/// Represents a person's name. Mapped as EF Core Complex Type.
/// </summary>
public record PersonName
{
    public required string FirstName { get; init; }
    public string? LastName { get; init; }
    public string FullName => string.IsNullOrWhiteSpace(LastName) ? FirstName : $"{FirstName} {LastName}";
}