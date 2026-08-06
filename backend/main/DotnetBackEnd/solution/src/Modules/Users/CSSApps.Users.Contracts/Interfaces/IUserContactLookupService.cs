using CSSApps.Users.Contracts.DTOs;

namespace CSSApps.Users.Contracts.Interfaces;

public interface IUserContactLookupService
{
    Task<Guid?> FindUserIdByPhoneAsync(
        string countryCode,
        string number,
        CancellationToken cancellationToken = default);

    Task<UserContactProfileDto?> GetUserContactByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
