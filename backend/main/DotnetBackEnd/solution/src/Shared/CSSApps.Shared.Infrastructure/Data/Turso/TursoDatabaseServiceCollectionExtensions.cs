using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSSApps.Shared.Infrastructure.Data.Turso;

public static class TursoDatabaseServiceCollectionExtensions
{
    /// <summary>Registers the shared database connection owner, the per-scope route, and the replica pull loop.</summary>
    public static IServiceCollection AddTursoDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(TursoDatabase))) return services;
        services.AddOptions<TursoDatabaseOptions>().Bind(configuration.GetSection(TursoDatabaseOptions.SectionName));
        services.TryAddSingleton<TursoDatabase>();
        services.TryAddScoped<DatabaseRoute>();
        services.TryAddScoped<CloudConnectionLease>();
        services.AddHostedService<ReplicaPullHostedService>();
        return services;
    }
}

/// <summary>Bootstraps the replica at startup and keeps pulling cloud changes in the background.</summary>
internal sealed class ReplicaPullHostedService(TursoDatabase database, ILogger<ReplicaPullHostedService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Block startup until the replica exists so the first read-only request never sees an empty database.
        await database.InitializeReplicaAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!database.IsCloud) return;
        using var timer = new PeriodicTimer(database.PullInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await database.PullAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "[database] background replica pull failed; reads may be stale until the cloud is reachable");
            }
        }
    }
}
