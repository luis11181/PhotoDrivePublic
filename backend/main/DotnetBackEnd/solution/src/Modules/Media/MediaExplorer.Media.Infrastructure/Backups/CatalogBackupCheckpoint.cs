using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>Tracks the latest remotely verified backup and a durable, sanitized failure streak.</summary>
internal sealed class CatalogBackupCheckpoint(IConfiguration configuration, TimeProvider timeProvider)
{
    private readonly string _directory = Path.GetFullPath(configuration["MediaStorage:LocalDisk:BackupDirectory"] ?? "media-explorer.backups");
    private string VerifiedPath => Path.Combine(_directory, "last-remote-verified.utc");
    private string FailurePath => Path.Combine(_directory, "last-backup-failure.txt");

    public bool HasFreshVerifiedBackup(TimeSpan maximumAge)
        => GetStatus(maximumAge).IsFresh;

    public CatalogBackupCheckpointStatus GetStatus(TimeSpan maximumAge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumAge, TimeSpan.Zero);

        try
        {
            if (!File.Exists(VerifiedPath)) return CatalogBackupCheckpointStatus.Missing;
            var value = File.ReadAllText(VerifiedPath, Encoding.UTF8).Trim();
            if (!DateTimeOffset.TryParseExact(
                    value,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var verifiedAt))
            {
                return CatalogBackupCheckpointStatus.Missing;
            }

            var age = timeProvider.GetUtcNow() - verifiedAt;
            return age < TimeSpan.Zero
                ? CatalogBackupCheckpointStatus.Missing
                : new CatalogBackupCheckpointStatus(verifiedAt, age, age < maximumAge);
        }
        catch (IOException)
        {
            return CatalogBackupCheckpointStatus.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return CatalogBackupCheckpointStatus.Missing;
        }
    }

    public async Task MarkVerifiedAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(_directory, $".{Guid.CreateVersion7():N}.checkpoint.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, VerifiedPath, overwrite: true);
            if (File.Exists(FailurePath)) File.Delete(FailurePath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public CatalogBackupFailureStatus GetFailureStatus()
    {
        try
        {
            if (!File.Exists(FailurePath)) return CatalogBackupFailureStatus.None;
            var lines = File.ReadAllLines(FailurePath, Encoding.UTF8);
            return lines.Length == 3
                && DateTimeOffset.TryParseExact(lines[0], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var failedAt)
                && int.TryParse(lines[1], CultureInfo.InvariantCulture, out var count)
                ? new CatalogBackupFailureStatus(failedAt, Math.Max(1, count), lines[2])
                : CatalogBackupFailureStatus.None;
        }
        catch (IOException) { return CatalogBackupFailureStatus.None; }
        catch (UnauthorizedAccessException) { return CatalogBackupFailureStatus.None; }
    }

    public async Task MarkFailedAsync(string code, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Directory.CreateDirectory(_directory);
        var previous = GetFailureStatus();
        var value = string.Join('\n',
            timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            (previous.ConsecutiveFailureCount + 1).ToString(CultureInfo.InvariantCulture),
            code);
        var temporaryPath = Path.Combine(_directory, $".{Guid.CreateVersion7():N}.failure.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, value, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, FailurePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

internal readonly record struct CatalogBackupCheckpointStatus(
    DateTimeOffset? LastRemoteVerifiedAt,
    TimeSpan? Age,
    bool IsFresh)
{
    public static CatalogBackupCheckpointStatus Missing => new(null, null, false);
}

internal readonly record struct CatalogBackupFailureStatus(DateTimeOffset? LastFailureAt, int ConsecutiveFailureCount, string? Code)
{
    public static CatalogBackupFailureStatus None => new(null, 0, null);
}
