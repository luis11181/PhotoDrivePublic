using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TursoConnection = Turso.Data.Sqlite.SqliteConnection;

namespace CSSApps.Shared.Infrastructure.Data.Turso;

/// <summary>Sanitized database health for owner diagnostics. Never contains URLs, tokens, or paths.</summary>
public sealed record DatabaseSyncStatus(
    bool IsCloud,
    DateTimeOffset? LastPullAt,
    DateTimeOffset? LastPullFailureAt,
    string? LastPullError,
    int ConsecutivePullFailures,
    DateTimeOffset? LastCloudFailureAt,
    string? LastCloudError,
    long UnpushedLocalChanges);

/// <summary>
/// Owns how PhotoDrive reaches its single database. Writes and authoritative reads use a direct Turso Cloud
/// connection; read-only requests use this host's pull-only replica, refreshed in the background and after every
/// mutating request. In local mode (tests/tools) both routes open the same local Turso database file.
/// </summary>
public sealed class TursoDatabase : IAsyncDisposable
{
    public const string UsersHistoryTable = "__EFMigrationsHistory_Users";
    public const string MediaHistoryTable = "__EFMigrationsHistory_Media";

    private readonly TursoDatabaseOptions _options;
    private readonly ILogger<TursoDatabase> _logger;
    private readonly SemaphoreSlim _pullGate = new(1, 1);
    private readonly object _statusLock = new();
    private TursoConnection? _syncConnection;
    private long _lastSuccessfulPullStartedTimestamp;
    private DateTimeOffset? _lastPullAt;
    private DateTimeOffset? _lastPullFailureAt;
    private string? _lastPullError;
    private int _consecutivePullFailures;
    private DateTimeOffset? _lastCloudFailureAt;
    private string? _lastCloudError;

    public TursoDatabase(IOptions<TursoDatabaseOptions> options, ILogger<TursoDatabase> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (string.IsNullOrWhiteSpace(_options.Url))
            throw new InvalidOperationException("Database:Url is not configured. Set it to the Turso Cloud URL (or a local Turso file for tests).");
        IsCloud = _options.Url.StartsWith("turso://", StringComparison.OrdinalIgnoreCase)
                  || _options.Url.StartsWith("libsql://", StringComparison.OrdinalIgnoreCase);
        if (IsCloud && (string.IsNullOrWhiteSpace(_options.AuthToken) || _options.AuthToken.StartsWith("replace-", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Database:AuthToken is required for Turso Cloud. Put it in the untracked secrets.<Environment>.json next to appsettings.json (see secrets.example.json), or set Database__AuthToken.");
        if (IsCloud && string.IsNullOrWhiteSpace(_options.ReplicaPath))
            throw new InvalidOperationException("Database:ReplicaPath is required for Turso Cloud.");
    }

    public bool IsCloud { get; }

    public TimeSpan PullInterval => TimeSpan.FromSeconds(Math.Clamp(_options.PullIntervalSeconds, 5, 3600));

    private string CloudConnectionString => $"Data Source={_options.Url};Auth Token={_options.AuthToken}";

    private string ReplicaConnectionString => $"Data Source={_options.Url};Auth Token={_options.AuthToken};Replica Path={Path.GetFullPath(_options.ReplicaPath!)}";

    private string LocalConnectionString => $"Data Source={_options.Url}";

    /// <summary>
    /// Configures an EF context for the given route, keeping one open connection for the scope. With a
    /// <paramref name="lease"/>, cloud connections are borrowed from a small pool instead of opened, so requests reuse
    /// an established HTTPS connection (the driver otherwise pays a TLS handshake per connection).
    /// </summary>
    public void Configure(DbContextOptionsBuilder builder, DatabaseRouteKind route, string migrationsHistoryTable, CloudConnectionLease? lease = null)
    {
        var pooled = lease is not null && IsCloud && route == DatabaseRouteKind.Cloud;
        var connection = pooled ? lease!.Rent(this) : OpenConnection(route);
        builder.UseTurso(connection, contextOwnsConnection: !pooled, sqlite => sqlite.MigrationsHistoryTable(migrationsHistoryTable));
        // EF's SQLite provider warns on every context that it cannot register its ef_* helper functions on Turso's
        // connection type. PhotoDrive uses neither decimal columns nor REGEXP, which are what those helpers serve.
        builder.ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.SqliteEventId.UnexpectedConnectionTypeWarning));
        if (route == DatabaseRouteKind.Replica) builder.AddInterceptors(ReplicaReadOnlyInterceptor.Instance);
        else if (IsCloud) builder.AddInterceptors(CloudForeignKeysInterceptor.Instance);
    }

    private const int MaxPooledCloudConnections = 8;
    // Turso keeps an idle remote session for several seconds; within this window a pooled session is still valid.
    private static readonly TimeSpan PooledSessionFreshFor = TimeSpan.FromSeconds(3);
    private readonly System.Collections.Concurrent.ConcurrentBag<(TursoConnection Connection, long ReturnedAt)> _cloudPool = [];

    internal TursoConnection RentCloudConnection()
    {
        while (_cloudPool.TryTake(out var pooled))
        {
            if (Stopwatch.GetElapsedTime(pooled.ReturnedAt) < PooledSessionFreshFor) return pooled.Connection;
            try
            {
                // Validates the connection and re-enables foreign keys in case Turso replaced an idle session.
                EnableForeignKeys(pooled.Connection);
                CloudForeignKeysInterceptor.Instance.Touch(pooled.Connection);
                return pooled.Connection;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                pooled.Connection.Dispose();
            }
        }

        return OpenConnection(DatabaseRouteKind.Cloud);
    }

    internal void ReturnCloudConnection(TursoConnection connection)
    {
        if (connection.State == System.Data.ConnectionState.Open && _cloudPool.Count < MaxPooledCloudConnections)
            _cloudPool.Add((connection, Stopwatch.GetTimestamp()));
        else connection.Dispose();
    }

    private static void EnableForeignKeys(TursoConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    /// <summary>Opens a raw connection for the route. Callers own and dispose it.</summary>
    public TursoConnection OpenConnection(DatabaseRouteKind route)
    {
        TursoConnection connection = IsCloud && route == DatabaseRouteKind.Cloud
            ? new CloudSqliteConnection(ConnectionString(route))
            : new TursoConnection(ConnectionString(route));
        connection.Open();
        // Foreign keys (and ON DELETE CASCADE) are off by default, including on Turso Cloud sessions. They are not
        // set through the connection string because the driver would also apply them to EF's read-only probe
        // connection, which Turso rejects. Replicas never write, so they do not need them.
        if (route == DatabaseRouteKind.Cloud || !IsCloud) EnableForeignKeys(connection);
        if (!IsCloud)
        {
            // Local files (tests, tools) have one writer at a time; wait instead of failing with "database is locked".
            // Turso Cloud serializes writers itself and does not support this pragma.
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 10000;";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    private string ConnectionString(DatabaseRouteKind route) => !IsCloud
        ? LocalConnectionString
        : route == DatabaseRouteKind.Cloud ? CloudConnectionString : ReplicaConnectionString;

    /// <summary>Creates or opens this host's replica, downloading it from the cloud when absent.</summary>
    public async Task InitializeReplicaAsync(CancellationToken cancellationToken)
    {
        if (!IsCloud || _syncConnection is not null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.ReplicaPath!))!);
        var started = Stopwatch.GetTimestamp();
        var connection = new TursoConnection(ReplicaConnectionString);
        await connection.OpenAsync(cancellationToken);
        _syncConnection = connection;
        await PullAsync(cancellationToken);
        _logger.LogInformation("[database] replica ready elapsedMs={ElapsedMs}", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// Pulls committed cloud changes into the replica. Concurrent callers share one pull: a caller is satisfied by
    /// any successful pull that started after it asked, so bursts of writes cause at most one extra round trip.
    /// </summary>
    public async Task PullAsync(CancellationToken cancellationToken)
    {
        if (!IsCloud) return;
        var requested = Stopwatch.GetTimestamp();
        await _pullGate.WaitAsync(cancellationToken);
        try
        {
            if (Interlocked.Read(ref _lastSuccessfulPullStartedTimestamp) > requested) return;
            var started = Stopwatch.GetTimestamp();
            var connection = _syncConnection ?? throw new InvalidOperationException("The database replica has not been initialized.");
            try
            {
                try
                {
                    await connection.SyncAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && exception.InnerException is HttpRequestException)
                {
                    // Turso closes idle keep-alive connections; a pull that reused one fails once and succeeds on retry.
                    await connection.SyncAsync(cancellationToken);
                }
                Interlocked.Exchange(ref _lastSuccessfulPullStartedTimestamp, started);
                lock (_statusLock)
                {
                    _lastPullAt = DateTimeOffset.UtcNow;
                    _consecutivePullFailures = 0;
                    _lastPullError = null;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_statusLock)
                {
                    _lastPullFailureAt = DateTimeOffset.UtcNow;
                    _lastPullError = exception.GetType().Name;
                    _consecutivePullFailures++;
                }
                throw;
            }
        }
        finally
        {
            _pullGate.Release();
        }
    }

    private readonly object _pendingPullLock = new();
    private Task _pendingPull = Task.CompletedTask;

    /// <summary>
    /// Starts a pull for a write that just committed without making the writer wait for it. The next read-only
    /// request on this host waits for it (see <see cref="WaitForPendingPullAsync"/>), so it sees the change.
    /// </summary>
    public void SchedulePullAfterWrite()
    {
        if (!IsCloud) return;
        lock (_pendingPullLock) _pendingPull = PullAfterWriteAsync();
    }

    /// <summary>Waits (bounded) for a pull started by a recent write, so this host reads its own writes.</summary>
    public async Task WaitForPendingPullAsync()
    {
        Task pending;
        lock (_pendingPullLock) pending = _pendingPull;
        if (pending.IsCompleted) return;
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(_options.PullAfterWriteTimeoutSeconds, 1, 30)));
        }
        catch (TimeoutException)
        {
            // The replica catches up on its next pull; serve the read now.
        }
    }

    /// <summary>Pull after a write, bounded so a slow pull never blocks a response that already committed.</summary>
    public async Task PullAfterWriteAsync()
    {
        if (!IsCloud) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_options.PullAfterWriteTimeoutSeconds, 1, 30)));
        try
        {
            await PullAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "[database] post-write replica pull failed; the replica will catch up on the next pull");
        }
    }

    /// <summary>Records a failed cloud round trip so owners can see it in diagnostics.</summary>
    public void ReportCloudFailure(Exception exception)
    {
        lock (_statusLock)
        {
            _lastCloudFailureAt = DateTimeOffset.UtcNow;
            _lastCloudError = exception.GetType().Name;
        }
    }

    public async Task<DatabaseSyncStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        long unpushed = 0;
        if (_syncConnection?.SyncDatabase is { } sync)
            unpushed = (await sync.GetStatsAsync(cancellationToken)).CdcOperations;
        lock (_statusLock)
        {
            return new DatabaseSyncStatus(
                IsCloud,
                _lastPullAt,
                _lastPullFailureAt,
                _lastPullError,
                _consecutivePullFailures,
                _lastCloudFailureAt,
                _lastCloudError,
                unpushed);
        }
    }

    /// <summary>
    /// Writes a consistent, self-contained copy of the replica (or local database) to <paramref name="destinationPath"/>.
    /// </summary>
    public async Task SnapshotAsync(string destinationPath, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath)) throw new IOException("The snapshot destination already exists.");
        await using var connection = OpenConnection(DatabaseRouteKind.Replica);
        await using var command = connection.CreateCommand();
        // VACUUM INTO takes a literal; the path is generated by the backup service, never user input.
        command.CommandText = $"VACUUM INTO '{Path.GetFullPath(destinationPath).Replace("'", "''", StringComparison.Ordinal)}'";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        while (_cloudPool.TryTake(out var pooled)) await pooled.Connection.DisposeAsync();
        if (_syncConnection is not null) await _syncConnection.DisposeAsync();
        _pullGate.Dispose();
    }
}
