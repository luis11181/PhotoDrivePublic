using TursoConnection = Turso.Data.Sqlite.SqliteConnection;

namespace CSSApps.Shared.Infrastructure.Data.Turso;

/// <summary>
/// Per-scope record of the pooled cloud connections handed to this scope's DbContexts. They return to the pool
/// when the scope ends, after the contexts (created later, disposed first) have finished or rolled back their work.
/// </summary>
public sealed class CloudConnectionLease : IAsyncDisposable
{
    private readonly List<TursoConnection> _connections = [];
    private TursoDatabase? _database;
    private bool _discard;

    /// <summary>Dispose this scope's connections instead of returning them to the pool (for example after a failure).</summary>
    public void DiscardOnRelease() => _discard = true;

    internal TursoConnection Rent(TursoDatabase database)
    {
        _database = database;
        var connection = database.RentCloudConnection();
        _connections.Add(connection);
        return connection;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            if (_discard) connection.Dispose();
            else _database!.ReturnCloudConnection(connection);
        }
        _connections.Clear();
        return ValueTask.CompletedTask;
    }
}
