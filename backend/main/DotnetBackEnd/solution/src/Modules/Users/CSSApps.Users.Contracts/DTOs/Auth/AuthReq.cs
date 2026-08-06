using System.ComponentModel.DataAnnotations;
using CSSApps.Shared.Contracts.Constants;
using CSSApps.Users.Contracts.Constants;

namespace CSSApps.Users.Contracts.DTOs.Auth;

public class LoginReq
{
    [Required]
    [EmailAddress(ErrorMessage = AuthConstants.EmailValidationError)]
    public string Email { get; set; } = null!;

    [RegularExpression(AuthConstants.PasswordRegex, ErrorMessage = AuthConstants.PasswordValidationError)]
    public string? Password { get; set; }
}

public class RegisterReq
{
    [MinLength(AuthConstants.UsernameMinLength, ErrorMessage = AuthConstants.UsernameLengthValidationError)]
    public string? UserName { get; set; }

    [Required]
    [EmailAddress(ErrorMessage = AuthConstants.EmailValidationError)]
    public string Email { get; set; } = null!;

    [RegularExpression(AuthConstants.PasswordRegex, ErrorMessage = AuthConstants.PasswordValidationError)]
    public string? Password { get; set; }
}

/// <summary>Credential returned by Google Identity Services in the browser.</summary>
public class GoogleLoginReq
{
    [Required]
    public string Credential { get; set; } = null!;
}

public class SetPasswordReq
{
    public string? CurrentPassword { get; set; }

    [Required]
    [RegularExpression(AuthConstants.PasswordRegex, ErrorMessage = AuthConstants.PasswordValidationError)]
    public string Password { get; set; } = null!;
}

public class RefreshTokenReq
{
    [Required]
    [System.Text.Json.Serialization.JsonIgnore]
    public string RefreshToken { get; set; } = null!;
}

public class LogoutReq
{
    public bool LogoutFromAllDevices { get; set; }
}
