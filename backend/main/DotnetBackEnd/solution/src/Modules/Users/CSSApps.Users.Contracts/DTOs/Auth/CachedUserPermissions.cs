namespace CSSApps.Users.Contracts.DTOs.Auth;

public class CachedUserPermissions
{
    public required Guid UserId { get; set; }
    public IReadOnlyCollection<string> Roles { get; init; } = [];
    public IReadOnlyCollection<string> Permissions { get; init; } = [];
}