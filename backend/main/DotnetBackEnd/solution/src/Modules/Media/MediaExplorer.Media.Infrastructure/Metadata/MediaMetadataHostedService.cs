using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>Extracts metadata for pending uploads on the primary host.</summary>
internal sealed class MediaMetadataHostedService(
    IServiceScopeFactory scopeFactory,
    ClusterCoordinator cluster,
    MediaMetadataWorkSignal workSignal,
    ILogger<MediaMetadataHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[media.metadata] background processor started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Metadata is installation-wide work: only the primary host extracts it, so two hosts never race on the
                // same file. Another host takes over as soon as an owner makes it primary.
                if (!await cluster.IsPrimaryAsync(stoppingToken))
                {
                    await workSignal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var processed = await scope.ServiceProvider.GetRequiredService<FileMetadataProcessor>()
                    .ProcessPendingBatchAsync(4, stoppingToken);
                if (processed == 0) await workSignal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[media.metadata] background processor iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        logger.LogInformation("[media.metadata] background processor stopped");
    }
}
