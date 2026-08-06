using CSSApps.Users.Domain.Entities.UserAggregate;

namespace CSSApps.Users.Application.Services.Auth.OAuth;

internal sealed record OAuthAuthenticatedUser(User User, bool IsNewUser);
