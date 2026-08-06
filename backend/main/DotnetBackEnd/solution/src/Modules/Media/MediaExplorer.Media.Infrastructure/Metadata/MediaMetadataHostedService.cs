using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class MediaMetadataHostedService(
    IServiceScopeFactory scopeFactory,
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
