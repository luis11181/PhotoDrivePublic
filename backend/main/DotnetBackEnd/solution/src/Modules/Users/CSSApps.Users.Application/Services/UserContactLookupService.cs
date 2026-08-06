using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Contracts.DTOs;
using CSSApps.Users.Contracts.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CSSApps.Users.Application.Services;

internal sealed class UserContactLookupService(IUsersDbContext dbContext) : IUserContactLookupService
{
    public async Task<Guid?> FindUserIdByPhoneAsync(
        string countryCode,
        string number,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        var normalizedCountryCode = new string(countryCode.Where(char.IsDigit).ToArray());
        var normalizedNumber = new string(number.Where(char.IsDigit).ToArray());

        if (string.IsNullOrWhiteSpace(normalizedCountryCode) || string.IsNullOrWhiteSpace(normalizedNumber))
        {
            return null;
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Phone != null)
            .Where(user => user.Phone!.CountryCode == normalizedCountryCode && user.Phone.Number == normalizedNumber)
            .Select(user => (Guid?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<UserContactProfileDto?> GetUserContactByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new UserContactProfileDto(
                user.Id,
                user.Name.FullName,
                user.Email))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
