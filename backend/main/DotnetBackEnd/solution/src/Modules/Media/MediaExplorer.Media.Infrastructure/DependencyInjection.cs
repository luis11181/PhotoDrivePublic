using MediaExplorer.Media.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaExplorer.Media.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMediaInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MediaCatalog")
            ?? throw new InvalidOperationException("Connection string 'MediaCatalog' was not configured.");
        services.AddDbContext<MediaCatalogDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<MediaCatalogService>();
        services.AddScoped<LibraryBootstrapper>();
        services.AddScoped<CatalogAuthorization>();
        services.AddScoped<ISharingService, SharingService>();
        services.AddScoped<IAlbumService, AlbumService>();
        services.AddScoped<ITimelineEventService, TimelineEventService>();
        services.AddScoped<ICatalogBatchService, CatalogBatchService>();
        services.AddScoped<ITagService, TagService>();
        services.AddScoped<ICatalogQueryService>(provider => provider.GetRequiredService<MediaCatalogService>());
        services.AddScoped<ICatalogCommandService>(provider => provider.GetRequiredService<MediaCatalogService>());
        services.AddScoped<IUploadService>(provider => provider.GetRequiredService<MediaCatalogService>());
        services.AddScoped<IMediaDeliveryService>(provider => provider.GetRequiredService<MediaCatalogService>());
        services.AddSingleton<IObjectStoreFactory, S3ObjectStoreFactory>();
        services.AddSingleton<IOriginalCache, OriginalCache>();
        services.AddSingleton<MediaThumbnailCache>();
        services.AddSingleton<IMediaMetadataExtractor, ExternalMediaMetadataExtractor>();
        services.AddSingleton<MediaMetadataWorkSignal>();
        services.AddSingleton<ImportWorkSignal>();
        services.AddSingleton<CacheBuildWorkSignal>();
        services.AddScoped<FileMetadataProcessor>();
        services.AddScoped<ImportService>();
        services.AddScoped<IImportService>(provider => provider.GetRequiredService<ImportService>());
        services.AddScoped<CacheBuildService>();
        services.AddScoped<ICacheBuildService>(provider => provider.GetRequiredService<CacheBuildService>());
        services.AddScoped<MediaCatalogInitializer>();
        services.AddScoped<StorageReconciliationService>();
        services.AddScoped<CatalogBackupService>();
        services.AddScoped<UsersBackupService>();
        services.AddScoped<ICatalogBackupRemoteStore, S3CatalogBackupStore>();
        services.AddSingleton<CatalogBackupCheckpoint>();
        services.AddSingleton<CatalogBackupExecutionLock>();
        services.AddScoped<CatalogBackupPipeline>();
        services.AddScoped<IMediaMaintenanceService, MediaMaintenanceService>();
        services.AddHostedService<DailyCatalogBackupHostedService>();
        services.AddHostedService<MediaMetadataHostedService>();
        services.AddHostedService<ImportHostedService>();
        services.AddHostedService<CacheBuildHostedService>();
        return services;
    }
}
