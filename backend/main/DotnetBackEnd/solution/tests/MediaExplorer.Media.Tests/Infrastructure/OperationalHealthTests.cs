using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MediaExplorer.Media.Infrastructure;

public sealed class OperationalHealthTests
{
    [Fact]
    public async Task Backup_failure_streak_survives_restart_and_verified_backup_clears_it()
    {
        var root = Path.Combine(Path.GetTempPath(), $"media-explorer-health-tests-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MediaStorage:LocalDisk:BackupDirectory"] = root
            }).Build();
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
            var checkpoint = new CatalogBackupCheckpoint(configuration, time);

            await checkpoint.MarkFailedAsync("backup_pipeline_failed", CancellationToken.None);
            time.UtcNow = time.UtcNow.AddMinutes(5);
            await checkpoint.MarkFailedAsync("remote_verification_failed", CancellationToken.None);

            var afterRestart = new CatalogBackupCheckpoint(configuration, time).GetFailureStatus();
            afterRestart.ConsecutiveFailureCount.Should().Be(2);
            afterRestart.LastFailureAt.Should().Be(time.UtcNow);
            afterRestart.Code.Should().Be("remote_verification_failed");

            await checkpoint.MarkVerifiedAsync(CancellationToken.None);
            checkpoint.GetFailureStatus().Should().Be(CatalogBackupFailureStatus.None);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
