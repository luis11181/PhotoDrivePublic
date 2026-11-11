namespace CSSApps.Users.Contracts.Interfaces.Auth;

/// <summary>Reads a user's role names (Owner/Member) for request authorization, with a short cache.</summary>
public interface IUserRolesService
{
    Task<IReadOnlyCollection<string>?> GetUserRolesAsync(Guid userId);
    Task InvalidateUserRolesAsync(Guid userId);
}
