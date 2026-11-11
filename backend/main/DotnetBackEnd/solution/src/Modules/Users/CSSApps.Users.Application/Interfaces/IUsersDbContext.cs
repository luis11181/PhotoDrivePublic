using CSSApps.Users.Domain.Entities.UserAggregate;
using CSSApps.Shared.Contracts.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CSSApps.Users.Application.Interfaces;

public interface IUsersDbContext : IModuleDbContext
{
    //DbSet<BotPhoneNumber> botPhoneNumbers { get; }
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<UserToken> UserTokens { get; }
    DbSet<UserSession> UserSessions { get; }
    DbSet<SecurityToken> SecurityTokens { get; }
    DbSet<SecurityAuditEvent> SecurityAuditEvents { get; }
}
