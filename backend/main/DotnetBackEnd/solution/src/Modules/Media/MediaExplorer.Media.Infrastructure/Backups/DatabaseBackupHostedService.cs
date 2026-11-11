using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>
/// On the primary host only, runs the database backup when the last remotely verified backup (recorded in the
/// shared database) is older than <c>MediaStorage:Backups:IntervalDays</c> (7 by default). Checking hourly means an
/// overdue or failed backup is retried within an hour, whichever host is primary at the time.
/// </summary>
internal sealed class DatabaseBackupHostedService(
    IServiceScopeFactory scopeFactory,
    ClusterCoordinator cluster,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<DatabaseBackupHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    public static TimeSpan BackupInterval(IConfiguration configuration) =>
        TimeSpan.FromDays(Math.Clamp(configuration.GetValue("MediaStorage:Backups:IntervalDays", 7), 1, 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("MediaStorage:Backups:ScheduleEnabled", true))
        {
            logger.LogInformation("[media.backup] scheduled backups disabled");
            return;
        }

        await Task.Delay(StartupDelay, timeProvider, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await IsDueAsync(stoppingToken))
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<DatabaseBackupService>().RunAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[media.backup] scheduled backup check failed; retrying in an hour");
            }

            await Task.Delay(CheckInterval, timeProvider, stoppingToken);
        }
    }

    private async Task<bool> IsDueAsync(CancellationToken cancellationToken)
    {
        if (!await cluster.IsPrimaryAsync(cancellationToken)) return false;
        var settings = await cluster.GetSettingsAsync(cancellationToken);
        var lastVerified = settings.LastBackupVerifiedAt;
        return lastVerified is null || timeProvider.GetUtcNow().UtcDateTime - lastVerified.Value >= BackupInterval(configuration);
    }
}
