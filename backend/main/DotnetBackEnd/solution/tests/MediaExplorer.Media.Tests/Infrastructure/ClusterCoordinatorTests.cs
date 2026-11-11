using CSSApps.Shared.Infrastructure.Data.Turso;
using FluentAssertions;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class ClusterCoordinatorTests
{
    [Fact]
    public async Task First_host_is_primary_and_an_online_host_takes_over_when_the_primary_goes_quiet()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var provider = Services(harness);
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var time = new ManualTime(DateTimeOffset.Parse("2026-11-09T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var desktop = new ClusterCoordinator(scopes, new HostIdentity(Guid.NewGuid(), "desktop"), time);
        var laptop = new ClusterCoordinator(scopes, new HostIdentity(Guid.NewGuid(), "laptop"), time);

        await desktop.HeartbeatAsync(CancellationToken.None);
        await laptop.HeartbeatAsync(CancellationToken.None);
        (await desktop.IsPrimaryAsync(CancellationToken.None)).Should().BeTrue();
        (await laptop.IsPrimaryAsync(CancellationToken.None)).Should().BeFalse();

        // The desktop is switched off: it stops sending heartbeats while the laptop keeps running.
        time.Now += ClusterCoordinator.PrimaryTakeoverAfter - TimeSpan.FromMinutes(1);
        await laptop.HeartbeatAsync(CancellationToken.None);
        (await laptop.GetSettingsAsync(CancellationToken.None)).PrimaryHostId.Should().Be(desktop.Host.Id, "a short pause must not move the role");

        time.Now += TimeSpan.FromMinutes(2);
        await laptop.HeartbeatAsync(CancellationToken.None);
        (await laptop.IsPrimaryAsync(CancellationToken.None)).Should().BeTrue();

        // When the desktop comes back it does not take the role back while the laptop is online.
        await desktop.HeartbeatAsync(CancellationToken.None);
        (await desktop.GetSettingsAsync(CancellationToken.None)).PrimaryHostId.Should().Be(laptop.Host.Id);
    }

    private static ServiceProvider Services(MediaTestHarness harness)
    {
        var services = new ServiceCollection();
        services.AddScoped<DatabaseRoute>();
        services.AddDbContext<MediaCatalogDbContext>(options =>
            harness.Database.Configure(options, DatabaseRouteKind.Cloud, TursoDatabase.MediaHistoryTable));
        return services.BuildServiceProvider();
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
