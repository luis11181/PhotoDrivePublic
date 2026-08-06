using CSSApps.Shared.Contracts.Primitives;

namespace CSSApps.Shared.Contracts.ValueObjects;

public class Address : ValueObject
{
    public required string Street { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? PostalCode { get; init; }
    public string? Country { get; init; }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Street.ToLower();
        if (City is not null)
            yield return City.ToLower();
        if (State is not null)
            yield return State.ToLower();
        if (PostalCode is not null)
            yield return PostalCode.ToLower();
        if (Country is not null)
            yield return Country.ToLower();
    }
}