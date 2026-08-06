using CSSApps.Shared.Infrastructure.Data.DesignTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CSSApps.Users.Infrastructure.Data;

public sealed class UsersDbContextFactory : IDesignTimeDbContextFactory<UsersDbContext>
{
    public UsersDbContext CreateDbContext(string[] args)
    {
        var connectionString = DesignTimeDbContextFactorySupport.GetRequiredConnectionString("UsersModule");
        var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();
        optionsBuilder.UseSqlite(connectionString);

        return new UsersDbContext(optionsBuilder.Options, DesignTimeDbContextFactorySupport.ExecutionContextAccessor);
    }
}
