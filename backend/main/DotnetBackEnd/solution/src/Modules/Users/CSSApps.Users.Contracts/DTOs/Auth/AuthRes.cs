using System.Text.Json.Serialization;

namespace CSSApps.Users.Contracts.DTOs.Auth;

public class AuthRes
{
    public Guid Id { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Username { get; set; }
    [JsonIgnore] // access token is returned only in an HttpOnly cookie
    public string JwtToken { get; set; } = null!;

    public CurrentUserRes User { get; set; } = null!;

    [JsonIgnore] // refresh token is returned in http only cookie
    public string? RefreshToken { get; set; }
}

/// <summary>Safe account data returned to the browser; it never contains credentials or token material.</summary>
public class CurrentUserRes
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public bool HasPassword { get; set; }
    public bool EmailVerified { get; set; }
    public bool IsOwner { get; set; }
    public bool GoogleLoginAllowed { get; set; }
    public bool PasswordLoginAllowed { get; set; }
    public bool MustChangePassword { get; set; }
}

public class AuthSessionRes
{
    public CurrentUserRes User { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
}
