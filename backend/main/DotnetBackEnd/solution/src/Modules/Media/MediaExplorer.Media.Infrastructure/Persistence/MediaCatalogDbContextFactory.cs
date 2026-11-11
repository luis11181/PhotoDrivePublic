using CSSApps.Shared.Infrastructure.Data.DesignTime;
using CSSApps.Shared.Infrastructure.Data.Turso;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MediaExplorer.Media.Infrastructure;

public sealed class MediaCatalogDbContextFactory : IDesignTimeDbContextFactory<MediaCatalogDbContext>
{
    public MediaCatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MediaCatalogDbContext>()
            .UseTurso(DesignTimeDbContextFactorySupport.LocalConnectionString(), sqlite => sqlite.MigrationsHistoryTable(TursoDatabase.MediaHistoryTable))
            .Options;
        return new MediaCatalogDbContext(options);
    }
}
