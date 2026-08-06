using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class CacheBuildHostedService(
    IServiceScopeFactory scopeFactory,
    CacheBuildWorkSignal workSignal,
    ILogger<CacheBuildHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[media.cache-build] background processor started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processed = await scope.ServiceProvider.GetRequiredService<CacheBuildService>()
                    .ProcessPendingBatchAsync(2, stoppingToken);
                if (processed == 0) await workSignal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("[media.cache-build] worker iteration failed exceptionType={ExceptionType}", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        logger.LogInformation("[media.cache-build] background processor stopped");
    }
}
