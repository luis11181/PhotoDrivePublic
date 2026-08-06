using System.Reflection;
using CSSApps.Shared.Contracts.Interfaces;
using CSSApps.Shared.Infrastructure.Data;
using CSSApps.Shared.Infrastructure.Data.Interceptors;
using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Domain.Entities;
using CSSApps.Users.Domain.Entities.UserAggregate;
using CSSApps.Users.Infrastructure.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace CSSApps.Users.Infrastructure.Data;

public class UsersDbContext(
    DbContextOptions<UsersDbContext> options,
    IExecutionContextAccessor executionContextAccessor)
    : ModuleDbContext<UsersDbContext>(options), IUsersDbContext //,IApplicationDbContext
{
    public virtual DbSet<User> Users { get; init; }
    public virtual DbSet<Role> Roles { get; init; }
    public virtual DbSet<Permission> Permissions { get; init; }
    public virtual DbSet<UserToken> UserTokens { get; init; }
    public virtual DbSet<UserSession> UserSessions { get; init; }
    public virtual DbSet<SecurityToken> SecurityTokens { get; init; }
    public virtual DbSet<SecurityAuditEvent> SecurityAuditEvents { get; init; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        //Scans the current assembly (DLL) for all classes that implement IEntityTypeConfiguration<T>
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        modelBuilder.Seed();

        base.OnModelCreating(modelBuilder);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(new AuditableEntityInterceptor(executionContextAccessor));


        //get the enviroment variable to only log the sql queries in development
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        if (env == "Development"){
            //REMIND: this logs all ef core just for debugging purposes
            //optionsBuilder.EnableSensitiveDataLogging();
        //REMIND: remove this in production, gives more details but has production overhead
        //       optionsBuilder.EnableDetailedErrors();
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.ConfigGlobalEnumConversions();
    }

}
