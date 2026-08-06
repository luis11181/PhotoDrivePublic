using CSSApps.Shared.Contracts.Primitives;

namespace CSSApps.Users.Domain.Entities.UserAggregate.ValueObjects;

/// <summary>
/// Represents a phone number. Mapped as EF Core Complex Type.
/// Use the record initializer for trusted data (from DB, internal systems).
/// Use CreateValidated() when accepting user input that needs validation.
/// </summary>
public record PhoneNumber
{
    public required string CountryCode { get; init; }
    public required string Number { get; init; }

    public string FullNumber => $"+{CountryCode}{Number}";

    /// <summary>
    /// Creates a validated PhoneNumber from user input.
    /// Use this when accepting phone numbers from external/untrusted sources.
    /// </summary>
    public static Result<PhoneNumber> CreateValidated(string countryCode, string number)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            return new ResultError("Country code is required");

        if (!countryCode.All(char.IsDigit))
            return new ResultError("Country code must contain only digits");

        if (countryCode.Length < 1 || countryCode.Length > 4)
            return new ResultError("Country code must be 1-4 digits");

        if (string.IsNullOrWhiteSpace(number))
            return new ResultError("Phone number is required");

        // Clean the number - remove common formatting characters
        var cleanNumber = new string(number.Where(char.IsDigit).ToArray());

        if (cleanNumber.Length < 4 || cleanNumber.Length > 15)
            return new ResultError("Phone number must be between 4 and 15 digits");

        return new PhoneNumber
        {
            CountryCode = countryCode.Trim(),
            Number = cleanNumber
        };
    }
}