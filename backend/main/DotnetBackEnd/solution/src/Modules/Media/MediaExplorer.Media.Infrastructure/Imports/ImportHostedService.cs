using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class ImportHostedService(
    IServiceScopeFactory scopeFactory,
    ClusterCoordinator cluster,
    ImportWorkSignal workSignal,
    TimeProvider timeProvider,
    ILogger<ImportHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    private DateTimeOffset _nextPruneAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[media.import] background processor started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var imports = scope.ServiceProvider.GetRequiredService<ImportService>();
                await PruneFinishedJobsIfDueAsync(imports, stoppingToken);
                var processed = await imports.ProcessPendingBatchAsync(2, stoppingToken);
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

    // Retention is installation-wide work, so only the primary host runs it (hourly).
    private async Task PruneFinishedJobsIfDueAsync(ImportService imports, CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextPruneAt) return;
        _nextPruneAt = now + PruneInterval;
        if (!await cluster.IsPrimaryAsync(stoppingToken)) return;
        await imports.PruneFinishedJobsAsync(now.UtcDateTime, stoppingToken);
    }
}
