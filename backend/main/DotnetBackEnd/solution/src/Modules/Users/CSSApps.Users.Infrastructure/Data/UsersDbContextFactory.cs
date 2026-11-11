using CSSApps.Shared.Infrastructure.Data.DesignTime;
using CSSApps.Shared.Infrastructure.Data.Turso;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CSSApps.Users.Infrastructure.Data;

public sealed class UsersDbContextFactory : IDesignTimeDbContextFactory<UsersDbContext>
{
    public UsersDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();
        optionsBuilder.UseTurso(DesignTimeDbContextFactorySupport.LocalConnectionString(), sqlite => sqlite.MigrationsHistoryTable(TursoDatabase.UsersHistoryTable));

        return new UsersDbContext(optionsBuilder.Options, DesignTimeDbContextFactorySupport.ExecutionContextAccessor);
    }
}
