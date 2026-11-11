namespace CSSApps.Shared.Infrastructure.Data.Turso;

public enum DatabaseRouteKind
{
    /// <summary>Direct connection to Turso Cloud: authoritative reads and every write.</summary>
    Cloud,

    /// <summary>This host's pull-only local replica: fast, possibly slightly stale, read-only.</summary>
    Replica
}

/// <summary>
/// Per-scope choice of database connection. Scopes default to <see cref="DatabaseRouteKind.Cloud"/>, so background
/// work and commands are always authoritative; the HTTP pipeline switches read-only requests to the replica before
/// any DbContext is created.
/// </summary>
public sealed class DatabaseRoute
{
    public DatabaseRouteKind Kind { get; private set; } = DatabaseRouteKind.Cloud;

    public bool IsReplica => Kind == DatabaseRouteKind.Replica;

    public void UseReplica() => Kind = DatabaseRouteKind.Replica;
}
