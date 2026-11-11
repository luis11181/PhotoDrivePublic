using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>
/// Coordinates the hosts sharing one database through the cloud row <see cref="ClusterSettings"/>: which host is
/// primary (runs metadata extraction and scheduled backups) and the latest backup outcome. The first host that
/// starts becomes primary; an owner can move the role to another host at any time, for example before turning one
/// off. Background scopes use the authoritative cloud route by default.
/// </summary>
public sealed class ClusterCoordinator(IServiceScopeFactory scopeFactory, HostIdentity host, TimeProvider timeProvider)
{
    private static readonly TimeSpan PrimaryCacheDuration = TimeSpan.FromSeconds(30);
    private readonly object _cacheLock = new();
    private readonly SemaphoreSlim _heartbeatGate = new(1, 1);
    private (bool IsPrimary, DateTimeOffset CheckedAt)? _primaryCache;

    public HostIdentity Host => host;

    /// <summary>Records this host as alive, and makes it primary when there is no primary or the primary has gone offline.</summary>
    public async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        await _heartbeatGate.WaitAsync(cancellationToken);
        try
        {
            await HeartbeatOnceAsync(cancellationToken);
        }
        finally
        {
            _heartbeatGate.Release();
        }
    }

    private async Task HeartbeatOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaCatalogDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = await db.Hosts.SingleOrDefaultAsync(candidate => candidate.Id == host.Id, cancellationToken);
        if (row is null)
        {
            db.Hosts.Add(new ServerHost { Id = host.Id, Name = host.Name, FirstSeenAt = now, LastSeenAt = now });
        }
        else
        {
            row.Name = host.Name;
            row.LastSeenAt = now;
        }

        var settings = await db.Cluster.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (settings is null) db.Cluster.Add(new ClusterSettings { PrimaryHostId = host.Id, PrimaryChangedAt = now });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (settings is null || row is null)
        {
            // Another host created a shared row at the same moment; the next heartbeat converges.
            return;
        }

        if (settings is null) return;
        var previousPrimary = settings.PrimaryHostId;
        if (previousPrimary is not null && !await PrimaryIsGoneAsync(db, previousPrimary.Value, now, cancellationToken)) return;

        // Either computer can be turned off at any time: when the primary stops sending heartbeats, the next online
        // host takes over installation-wide work instead of leaving metadata and backups stalled. The update only
        // applies while the role is unchanged, so two hosts taking over at the same moment cannot both win.
        var claimed = await db.Cluster
            .Where(candidate => candidate.Id == settings.Id && candidate.PrimaryHostId == previousPrimary)
            .ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.PrimaryHostId, host.Id)
                .SetProperty(candidate => candidate.PrimaryChangedAt, now), cancellationToken);
        if (claimed > 0) lock (_cacheLock) _primaryCache = null;
    }

    /// <summary>How long the primary may go without a heartbeat before another online host takes over.</summary>
    public static readonly TimeSpan PrimaryTakeoverAfter = TimeSpan.FromMinutes(5);

    private async Task<bool> PrimaryIsGoneAsync(MediaCatalogDbContext db, Guid primaryHostId, DateTime now, CancellationToken cancellationToken)
    {
        if (primaryHostId == host.Id) return false;
        var lastSeen = await db.Hosts.AsNoTracking()
            .Where(candidate => candidate.Id == primaryHostId)
            .Select(candidate => (DateTime?)candidate.LastSeenAt)
            .SingleOrDefaultAsync(cancellationToken);
        return lastSeen is null || now - lastSeen.Value > PrimaryTakeoverAfter;
    }

    /// <summary>Whether this host currently owns installation-wide background work (cached briefly).</summary>
    public async Task<bool> IsPrimaryAsync(CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_primaryCache is { } cached && timeProvider.GetUtcNow() - cached.CheckedAt < PrimaryCacheDuration)
                return cached.IsPrimary;
        }

        var primary = await ReadPrimaryAsync(cancellationToken);
        if (primary is null)
        {
            await HeartbeatAsync(cancellationToken);
            primary = await ReadPrimaryAsync(cancellationToken);
        }

        var isPrimary = primary == host.Id;
        lock (_cacheLock) _primaryCache = (isPrimary, timeProvider.GetUtcNow());
        return isPrimary;
    }

    public async Task<IReadOnlyList<ServerHost>> ListHostsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaCatalogDbContext>();
        return await db.Hosts.AsNoTracking().OrderBy(candidate => candidate.Name).ThenBy(candidate => candidate.Id).ToListAsync(cancellationToken);
    }

    public async Task<ClusterSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaCatalogDbContext>();
        return await db.Cluster.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? new ClusterSettings();
    }

    /// <summary>Moves the primary role. Returns false when the host is unknown.</summary>
    public async Task<bool> SetPrimaryAsync(Guid hostId, Guid changedByUserId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaCatalogDbContext>();
        if (!await db.Hosts.AnyAsync(candidate => candidate.Id == hostId, cancellationToken)) return false;
        var settings = await db.Cluster.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new ClusterSettings();
            db.Cluster.Add(settings);
        }
        settings.PrimaryHostId = hostId;
        settings.PrimaryChangedAt = timeProvider.GetUtcNow().UtcDateTime;
        settings.PrimaryChangedByUserId = changedByUserId;
        await db.SaveChangesAsync(cancellationToken);
        lock (_cacheLock) _primaryCache = null;
        return true;
    }

    public Task RecordBackupSucceededAsync(string backupSetId, CancellationToken cancellationToken) =>
        UpdateSettingsAsync(settings =>
        {
            settings.LastBackupVerifiedAt = timeProvider.GetUtcNow().UtcDateTime;
            settings.LastBackupSetId = backupSetId;
            settings.LastBackupFailedAt = null;
            settings.LastBackupFailureCode = null;
            settings.BackupConsecutiveFailures = 0;
        }, cancellationToken);

    public Task RecordBackupFailedAsync(string code, CancellationToken cancellationToken) =>
        UpdateSettingsAsync(settings =>
        {
            settings.LastBackupFailedAt = timeProvider.GetUtcNow().UtcDateTime;
            settings.LastBackupFailureCode = code;
            settings.BackupConsecutiveFailures++;
        }, cancellationToken);

    private async Task<Guid?> ReadPrimaryAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaCatalogDbContext>();
        return await db.Cluster.AsNoTracking().Select(settings => settings.PrimaryHostId).SingleOrDefaultAsync(cancellationToken);
    }

    private async Task UpdateSettingsAsync(Action<ClusterSettings> update, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaCatalogDbContext>();
        var settings = await db.Cluster.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new ClusterSettings();
            db.Cluster.Add(settings);
        }
        update(settings);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Keeps this host's row fresh so owners can see which hosts are online.</summary>
internal sealed class HostHeartbeatHostedService(ClusterCoordinator cluster, ILogger<HostHeartbeatHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await cluster.HeartbeatAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "[cluster] heartbeat failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
