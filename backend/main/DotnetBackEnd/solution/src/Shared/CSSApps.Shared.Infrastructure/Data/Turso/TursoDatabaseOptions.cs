namespace CSSApps.Shared.Infrastructure.Data.Turso;

/// <summary>
/// Configuration for the single shared PhotoDrive database (section <c>Database</c>).
/// A <c>turso://</c>/<c>libsql://</c> URL selects Turso Cloud with a pull-only local replica; any other value is
/// treated as a local Turso database file (tests and offline tooling).
/// </summary>
public sealed class TursoDatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Turso Cloud URL (<c>turso://name.region.turso.io</c>) or a local database file path.</summary>
    public string? Url { get; set; }

    /// <summary>Database auth token. Supply it through <c>Database__AuthToken</c>; never commit it.</summary>
    public string? AuthToken { get; set; }

    /// <summary>Local file for this host's pull-only replica. Never shared between machines.</summary>
    public string? ReplicaPath { get; set; }

    /// <summary>Background replica pull interval.</summary>
    public int PullIntervalSeconds { get; set; } = 30;

    /// <summary>How long a mutating request waits for the post-write pull before responding anyway.</summary>
    public int PullAfterWriteTimeoutSeconds { get; set; } = 5;
}
