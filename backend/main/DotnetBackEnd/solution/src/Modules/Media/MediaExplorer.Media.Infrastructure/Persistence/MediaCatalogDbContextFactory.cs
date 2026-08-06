using CSSApps.Shared.Infrastructure.Data.DesignTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MediaExplorer.Media.Infrastructure;

public sealed class MediaCatalogDbContextFactory : IDesignTimeDbContextFactory<MediaCatalogDbContext>
{
    public MediaCatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString = DesignTimeDbContextFactorySupport.GetRequiredConnectionString("MediaCatalog");
        var options = new DbContextOptionsBuilder<MediaCatalogDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new MediaCatalogDbContext(options);
    }
}
