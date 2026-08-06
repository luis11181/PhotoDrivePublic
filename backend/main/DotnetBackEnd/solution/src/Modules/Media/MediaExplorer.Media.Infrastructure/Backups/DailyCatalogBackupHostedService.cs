using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>Runs only the catalog backup pipeline on a configured local-time interval; it never reconciles original blobs or evicts caches.</summary>
internal sealed class DailyCatalogBackupHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    CatalogBackupCheckpoint backupCheckpoint,
    ILogger<DailyCatalogBackupHostedService> logger) : BackgroundService
{
    private readonly bool _enabled = ParseBoolean(configuration["MediaStorage:Backups:ScheduleEnabled"], fallback: true);
    private readonly TimeOnly _anchorLocalTime = ParseTime(configuration["MediaStorage:Backups:AnchorLocalTime"], TimeOnly.MinValue);
    private readonly int _intervalHours = ParseIntervalHours(configuration["MediaStorage:Backups:IntervalHours"], fallback: 12);
    private readonly TimeZoneInfo _timeZone = ParseTimeZone(configuration["MediaStorage:Backups:TimeZoneId"] ?? "America/Bogota");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            logger.LogInformation("[media.backup] recurring schedule disabled");
            return;
        }

        var maximumBackupAge = TimeSpan.FromHours(_intervalHours);
        if (!backupCheckpoint.HasFreshVerifiedBackup(maximumBackupAge))
        {
            logger.LogInformation("[media.backup] no remotely verified backup exists within the last {MaximumBackupAgeHours} hours; running startup backup", _intervalHours);
            if (!await TryRunBackupAsync(stoppingToken)) return;
        }
        else
        {
            logger.LogInformation("[media.backup] recent remotely verified backup found; startup backup skipped");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var nextRun = NextRunUtc(now, _timeZone, _anchorLocalTime, _intervalHours);
            logger.LogInformation(
                "[media.backup] next recurring run scheduled nextRunUtc={NextRunUtc} anchorLocalTime={AnchorLocalTime} intervalHours={IntervalHours} timeZone={TimeZoneId}",
                nextRun,
                _anchorLocalTime,
                _intervalHours,
                _timeZone.Id);
            await Task.Delay(nextRun - now, timeProvider, stoppingToken);

            if (!await TryRunBackupAsync(stoppingToken)) return;
        }
    }

    private async Task<bool> TryRunBackupAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<CatalogBackupPipeline>().RunAsync(stoppingToken);
            if (result.RemoteBackupFailure is not null)
            {
                logger.LogError(
                    result.RemoteBackupFailure,
                    "[media.backup] recurring run failed during remote S3 copy after local verification snapshotPath={SnapshotPath}",
                    result.LocalBackup.SnapshotPath);
                return true;
            }

            logger.LogInformation(
                "[media.backup] recurring run completed backupSetId={BackupSetId} artifacts={ArtifactCount} sizeBytes={UploadedBytes}",
                result.RemoteBackup!.BackupSetId,
                result.RemoteBackup.ArtifactCount,
                result.RemoteBackup.UploadedBytes);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[media.backup] recurring run failed before remote verification completed");
            return true;
        }
    }

    internal static DateTimeOffset NextRunUtc(DateTimeOffset nowUtc, TimeZoneInfo timeZone, TimeOnly anchorLocalTime, int intervalHours)
    {
        if (intervalHours <= 0 || 24 % intervalHours != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalHours), "The backup interval must be a positive whole-hour divisor of 24.");
        }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var candidate = localNow.Date.Add(anchorLocalTime.ToTimeSpan());
        while (candidate <= localNow.DateTime) candidate = candidate.AddHours(intervalHours);
        candidate = DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(candidate)) candidate = candidate.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidate, timeZone), TimeSpan.Zero);
    }

    private static bool ParseBoolean(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static TimeOnly ParseTime(string? value, TimeOnly fallback) =>
        TimeOnly.TryParse(value, out var parsed) ? parsed : fallback;

    internal static int ParseIntervalHours(string? value, int fallback)
    {
        var intervalHours = int.TryParse(value, out var parsed) ? parsed : fallback;
        if (intervalHours <= 0 || 24 % intervalHours != 0)
        {
            throw new InvalidOperationException("MediaStorage:Backups:IntervalHours must be a positive whole-hour divisor of 24.");
        }
        return intervalHours;
    }

    private static TimeZoneInfo ParseTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InvalidOperationException($"MediaStorage:Backups:TimeZoneId '{id}' is not installed on this server.", exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new InvalidOperationException($"MediaStorage:Backups:TimeZoneId '{id}' is invalid on this server.", exception);
        }
    }
}
