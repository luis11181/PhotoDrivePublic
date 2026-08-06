namespace CSSApps.Users.Contracts.Constants;

public static class AuthConstants
{
    public const int UsernameMinLength = 9;

    public const string PasswordRegex = @"^(?=.*[A-Z])(?=.*[\W])(?=.*[0-9])(?=.*[a-z]).{12,128}$";

    public const string UsernameLengthValidationError = "Username must have more than 9 characters.";
    public const string EmailValidationError = "Email must have valid format.";

    public const string PasswordValidationError =
        "Password must have at least 12 characters, including uppercase, lowercase, number, and special character.";
}
