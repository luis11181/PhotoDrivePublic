using CSSApps.Shared.Contracts.Primitives;

namespace CSSApps.Users.Domain.Errors;

public static class AuthErrors
{
    public static readonly ResultError InvalidApiKey = new(
        "Invalid Api key", "INVALID_API_KEY");

    public static readonly ResultError InvalidGoogleToken = new(
        "Invalid Google token", "INVALID_GOOGLE_TOKEN");

    public static readonly ResultError OAuthAuthenticationFailed = new(
        "OAuth authentication failed", "OAUTH_AUTHENTICATION_FAILED");

    public static readonly ResultError InvalidCredentials = new(
        "Invalid email or password", "INVALID_CREDENTIALS");

    public static readonly ResultError UserNotFound = new(
        "User not found", "USER_NOT_FOUND");
}

public static class UserErrors
{
    // Phone number validation
    public static readonly ResultError InvalidPhoneCountryCode = new(
        "Invalid phone country code. Must contain only digits", "INVALID_PHONE_COUNTRY_CODE");

    public static readonly ResultError InvalidPhoneNumber = new(
        "Invalid phone number. Must contain only digits and optional hyphens", "INVALID_PHONE_NUMBER");

    public static readonly ResultError InvalidPhoneNumberLength = new(
        "Invalid phone number length. Must be between 4-15 digits", "INVALID_PHONE_NUMBER_LENGTH");
}