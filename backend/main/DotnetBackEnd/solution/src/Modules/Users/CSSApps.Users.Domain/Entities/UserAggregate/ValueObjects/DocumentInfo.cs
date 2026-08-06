namespace CSSApps.Users.Domain.Entities.UserAggregate.ValueObjects;

/// <summary>
/// Represents document identification info. Mapped as EF Core Complex Type.
/// </summary>
public record DocumentInfo
{
    public required string Type { get; init; }
    public required string Number { get; init; }
}