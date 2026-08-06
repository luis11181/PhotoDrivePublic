using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class ImportHostedService(
    IServiceScopeFactory scopeFactory,
    ImportWorkSignal workSignal,
    ILogger<ImportHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[media.import] background processor started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processed = await scope.ServiceProvider.GetRequiredService<ImportService>()
                    .ProcessPendingBatchAsync(2, stoppingToken);
                if (processed == 0) await workSignal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "[media.import] background processor iteration failed exceptionType={ExceptionType}",
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        logger.LogInformation("[media.import] background processor stopped");
    }
}
