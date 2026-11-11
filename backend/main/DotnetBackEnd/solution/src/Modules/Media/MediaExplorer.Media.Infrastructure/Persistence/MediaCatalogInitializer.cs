using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>
/// Applies Media migrations on request and keeps the derived search tables current. Search text lives in two
/// plain tables (<c>catalog_search</c>, <c>timeline_event_search</c>) holding <see cref="SearchText"/>-normalized
/// text for substring matching. <see cref="CatalogSearchIndex"/> updates them on every save; this class only creates
/// them and rebuilds them from the catalog when their version changes.
/// </summary>
public sealed class MediaCatalogInitializer
{
    internal const string CatalogSearchComponent = "catalog_search";

    /// <summary>Version 3 was SQLite FTS5 with triggers; later versions hold C#-normalized text maintained by EF saves.</summary>
    internal const int CatalogSearchSchemaVersion = 3 + SearchText.Version;

    private readonly MediaCatalogDbContext _dbContext;
    private readonly ILogger<MediaCatalogInitializer>? _logger;

    public MediaCatalogInitializer(
        MediaCatalogDbContext dbContext,
        ILogger<MediaCatalogInitializer>? logger = null)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ApplyMigrationsAsync(cancellationToken);
        await EnsureDerivedSchemaAsync(cancellationToken);
    }

    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Applying media catalog migrations.");
        await _dbContext.Database.MigrateAsync(cancellationToken);
    }

    public async Task EnsureDerivedSchemaAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRelationalPrerequisitesAsync(cancellationToken);
        await ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS derived_schema_versions (
                Component TEXT NOT NULL PRIMARY KEY,
                Version INTEGER NOT NULL CHECK (Version >= 1),
                AppliedAt TEXT NOT NULL
            );
            """,
            cancellationToken);
        if (await IsCatalogSearchCurrentAsync(cancellationToken))
        {
            _logger?.LogDebug("Derived catalog search schema is current at version {SchemaVersion}.", CatalogSearchSchemaVersion);
            return;
        }

        _logger?.LogInformation("Rebuilding derived catalog search schema at version {SchemaVersion}.", CatalogSearchSchemaVersion);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync("DROP TABLE IF EXISTS catalog_search;", cancellationToken);
        await ExecuteAsync("DROP TABLE IF EXISTS timeline_event_search;", cancellationToken);
        await ExecuteAsync(
            """
            CREATE TABLE catalog_search (
                item_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                library_id TEXT NOT NULL,
                name TEXT NOT NULL,
                tags TEXT NOT NULL,
                PRIMARY KEY (item_id, kind)
            );
            """,
            cancellationToken);
        await ExecuteAsync("CREATE INDEX ix_catalog_search_library ON catalog_search (library_id);", cancellationToken);
        await ExecuteAsync(
            """
            CREATE TABLE timeline_event_search (
                event_id TEXT NOT NULL PRIMARY KEY,
                library_id TEXT NOT NULL,
                title TEXT NOT NULL,
                description TEXT NOT NULL
            );
            """,
            cancellationToken);
        await ExecuteAsync("CREATE INDEX ix_timeline_event_search_library ON timeline_event_search (library_id);", cancellationToken);
        await CatalogSearchIndex.RebuildAsync(_dbContext, cancellationToken);
        await ExecuteAsync(
            $"""
            INSERT INTO derived_schema_versions (Component, Version, AppliedAt)
            VALUES ('{CatalogSearchComponent}', {CatalogSearchSchemaVersion}, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            ON CONFLICT(Component) DO UPDATE SET
                Version = excluded.Version,
                AppliedAt = excluded.AppliedAt;
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task EnsureRelationalPrerequisitesAsync(CancellationToken cancellationToken)
    {
        string[] requiredTables = ["folders", "files", "tags", "file_tags", "folder_tags", "timeline_events"];
        var missingTables = new List<string>();
        foreach (var table in requiredTables)
        {
            if (!await TableExistsAsync(table, cancellationToken)) missingTables.Add(table);
        }

        if (missingTables.Count == 0) return;

        throw new InvalidOperationException(
            "The Media catalog relational schema is not current. Missing required table(s): " +
            $"{string.Join(", ", missingTables)}. Apply the Media EF Core migrations before starting the host.");
    }

    private async Task<bool> IsCatalogSearchCurrentAsync(CancellationToken cancellationToken)
    {
        var version = await ScalarAsync(
            "SELECT Version FROM derived_schema_versions WHERE Component = $component;",
            cancellationToken,
            ("$component", CatalogSearchComponent));
        return version is not null
               && Convert.ToInt32(version, CultureInfo.InvariantCulture) == CatalogSearchSchemaVersion
               && await TableExistsAsync("catalog_search", cancellationToken)
               && await TableExistsAsync("timeline_event_search", cancellationToken);
    }

    private async Task<bool> TableExistsAsync(string name, CancellationToken cancellationToken) =>
        Convert.ToInt32(
            await ScalarAsync(
                "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = $name);",
                cancellationToken,
                ("$name", name)),
            CultureInfo.InvariantCulture) == 1;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "EF1002:Risk of vulnerability to SQL injection", Justification = "Only compile-time schema statements are executed.")]
    private Task ExecuteAsync(string sql, CancellationToken cancellationToken) =>
        _dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only compile-time schema queries with bound parameters are executed.")]
    private async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        var connection = _dbContext.Database.GetDbConnection();
        var shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose) await _dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction();
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            return await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose) await _dbContext.Database.CloseConnectionAsync();
        }
    }
}
