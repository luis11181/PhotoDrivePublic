using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Users.Contracts.DTOs.Auth;
using CSSApps.Users.Domain.Entities.UserAggregate;

namespace CSSApps.Users.Contracts.Interfaces.Auth;

public interface ILocalAuthenticationServices
{
    Task<Result<AuthRes>> Login(LoginReq model, string ipAddress, string userAgent);

    Task<Result<AuthRes>> GoogleLoginOrRegister(string credential, string ipAddress, string userAgent);

    Task<Result<CurrentUserRes>> SetPassword(Guid userId, SetPasswordReq model);

    Task<Result<CurrentUserRes>> GetCurrentUser(Guid userId);

    Task<Result<AuthRes>> RefreshToken(RefreshTokenReq model);

    Task<Result> Logout(Guid userId, Guid sessionId, bool logoutFromAllDevices);
}
