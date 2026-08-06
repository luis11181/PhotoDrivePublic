using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

public sealed class MediaCatalogInitializer
{
    internal const string CatalogSearchComponent = "catalog_search";
    internal const int CatalogSearchSchemaVersion = 3;

    private const int RequiredCatalogSearchObjectCount = 16;

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

        var hasVersionTable = await SchemaObjectExistsAsync(
            type: "table",
            name: "derived_schema_versions",
            cancellationToken);

        if (hasVersionTable && await IsCatalogSearchCurrentAsync(cancellationToken))
        {
            _logger?.LogDebug(
                "Derived catalog search schema is current at version {SchemaVersion}.",
                CatalogSearchSchemaVersion);
            await ApplyDatabasePragmasAsync(cancellationToken);
            return;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await EnsureDerivedSchemaVersionTableAsync(cancellationToken);

        // Recheck while holding the SQLite write transaction in case another host repaired it.
        if (!await IsCatalogSearchCurrentAsync(cancellationToken))
        {
            _logger?.LogInformation(
                "Rebuilding derived catalog search schema at version {SchemaVersion}.",
                CatalogSearchSchemaVersion);
            await RebuildCatalogSearchSchemaAsync(cancellationToken);
            await RecordCatalogSearchSchemaVersionAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await ApplyDatabasePragmasAsync(cancellationToken);
    }

    // SQLite FTS5 virtual tables and triggers are not part of EF Core's relational model.
    // Rebuild the derived index only when its version is stale or required objects are missing.
    private async Task RebuildCatalogSearchSchemaAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP TRIGGER IF EXISTS catalog_search_folders_insert;
            DROP TRIGGER IF EXISTS catalog_search_folders_update;
            DROP TRIGGER IF EXISTS catalog_search_folders_delete;
            DROP TRIGGER IF EXISTS catalog_search_files_insert;
            DROP TRIGGER IF EXISTS catalog_search_files_update;
            DROP TRIGGER IF EXISTS catalog_search_files_delete;
            DROP TRIGGER IF EXISTS catalog_search_file_tags_insert;
            DROP TRIGGER IF EXISTS catalog_search_file_tags_delete;
            DROP TRIGGER IF EXISTS catalog_search_folder_tags_insert;
            DROP TRIGGER IF EXISTS catalog_search_folder_tags_delete;
            DROP TRIGGER IF EXISTS catalog_search_tags_update;
            DROP TRIGGER IF EXISTS timeline_event_search_insert;
            DROP TRIGGER IF EXISTS timeline_event_search_update;
            DROP TRIGGER IF EXISTS timeline_event_search_delete;
            DROP TABLE IF EXISTS catalog_search;
            DROP TABLE IF EXISTS timeline_event_search;
            CREATE VIRTUAL TABLE catalog_search USING fts5(
                item_id UNINDEXED,
                library_id UNINDEXED,
                kind UNINDEXED,
                name,
                tags,
                tokenize = 'unicode61 remove_diacritics 2'
            );
            INSERT INTO catalog_search (item_id, library_id, kind, name, tags)
            SELECT folder.Id, folder.LibraryId, 'folder', folder.Name,
                COALESCE((SELECT group_concat(tag.Name, ' ') FROM folder_tags assignment JOIN tags tag ON tag.Id = assignment.TagId WHERE assignment.FolderId = folder.Id), '')
            FROM folders folder;
            INSERT INTO catalog_search (item_id, library_id, kind, name, tags)
            SELECT file.Id, file.LibraryId, 'file', file.Name,
                COALESCE((SELECT group_concat(tag.Name, ' ') FROM file_tags assignment JOIN tags tag ON tag.Id = assignment.TagId WHERE assignment.FileId = file.Id), '')
            FROM files file;
            CREATE VIRTUAL TABLE timeline_event_search USING fts5(
                event_id UNINDEXED,
                library_id UNINDEXED,
                title,
                description,
                tokenize = 'unicode61 remove_diacritics 2'
            );
            INSERT INTO timeline_event_search (event_id, library_id, title, description)
            SELECT Id, LibraryId, Title, COALESCE(Description, '')
            FROM timeline_events;
            CREATE TRIGGER IF NOT EXISTS catalog_search_folders_insert AFTER INSERT ON folders BEGIN
                INSERT INTO catalog_search (item_id, library_id, kind, name, tags) VALUES (new.Id, new.LibraryId, 'folder', new.Name, '');
            END;
            CREATE TRIGGER IF NOT EXISTS catalog_search_folders_update AFTER UPDATE ON folders BEGIN
                DELETE FROM catalog_search WHERE item_id = old.Id AND kind = 'folder';
                INSERT INTO catalog_search (item_id, library_id, kind, name, tags)
                VALUES (new.Id, new.LibraryId, 'folder', new.Name,
                    COALESCE((SELECT group_concat(tag.Name, ' ') FROM folder_tags assignment JOIN tags tag ON tag.Id = assignment.TagId WHERE assignment.FolderId = new.Id), ''));
            END;
            CREATE TRIGGER IF NOT EXISTS catalog_search_folders_delete AFTER DELETE ON folders BEGIN
                DELETE FROM catalog_search WHERE item_id = old.Id AND kind = 'folder';
            END;
            CREATE TRIGGER IF NOT EXISTS catalog_search_files_insert AFTER INSERT ON files BEGIN
                INSERT INTO catalog_search (item_id, library_id, kind, name, tags) VALUES (new.Id, new.LibraryId, 'file', new.Name, '');
            END;
            CREATE TRIGGER IF NOT EXISTS catalog_search_files_update AFTER UPDATE ON files BEGIN
                DELETE FROM catalog_search WHERE item_id = old.Id AND kind = 'file';
                INSERT INTO catalog_search (item_id, library_id, kind, name, tags)
                VALUES (new.Id, new.LibraryId, 'file', new.Name,
                    COALESCE((SELECT group_concat(tag.Name, ' ') FROM file_tags assignment JOIN tags tag ON tag.Id = assignment.TagId WHERE assignment.FileId = new.Id), ''));
            END;
            CREATE TRIGGER IF NOT EXISTS catalog_search_files_delete AFTER DELETE ON files BEGIN
                DELETE FROM catalog_search WHERE item_id = old.Id AND kind = 'file';
            END;
            CREATE TRIGGER catalog_search_file_tags_insert AFTER INSERT ON file_tags BEGIN
                DELETE FROM catalog_search WHERE item_id = new.FileId AND kind = 'file';
                INSERT INTO catalog_search (item_id, library_id, kind, name, tags)
                SELECT file.Id, file.LibraryId, 'file', file.Name,
                    COALESCE((SELECT group_concat(tag.Name, ' ') FROM file_tags assignment JOIN tags tag ON tag.Id = assignment.TagId WHERE assignment.FileId = file.Id), '')
                FROM files file WHERE file.Id = new.FileId;
            END;
            CREATE TRIGGER catalog_search_file_tags_delete AFTER DELETE ON file_tags BEGIN
                DELETE FROM catalog_search WHERE item_id = old.FileId AND kind = 'file';
                INSERT INTO catalog_search (item_id, library_id, kind, name, tags)
                SELECT file.Id, file.LibraryId, 'file', file.Name,
                    COALESCE((SELECT group_concat(tag.Name, ' ') FROM file_tags assignment JOIN tags tag ON tag.Id = assignment.TagId WHERE assignment.FileId = file.Id), '')
                FROM files file WHERE file.Id = old.FileId;
            END;
            CREATE TRIGGER catalog_search_folder_tags_insert AFTER INSERT ON folder_tags BEGIN
                DELETE FROM catalog_search WHERE item_id = new.FolderId AND kind = 'folder';
                INSERT INTO catalog_search (item_id, library_id, kind, name, tags)
                SELECT folder.Id, folder.LibraryId, 'folder', folder.Name,
                    COALESCE((SELECT group_concat(tag.Name, ' ') FROM folder_tags assignment JOIN tags tag ON tag.Id = assignment.TagId WHERE assignment.FolderId = folder.Id), '')
                FROM folders folder WHERE folder.Id = new.FolderId;
            END;
            CREATE TRIGGER catalog_search_folder_tags_delete AFTER DELETE ON folder_tags BEGIN
                DELETE FROM catalog_search WHERE item_id = old.FolderId AND kind = 'folder';
                INSERT INTO catalog_search (item_id, library_id, kind, name, tags)
                SELECT folder.Id, folder.LibraryId, 'folder', folder.Name,
                    COALESCE((SELECT group_concat(tag.Name, ' ') FROM folder_tags assignment JOIN tags tag ON tag.Id = assignment.TagId WHERE assignment.FolderId = folder.Id), '')
                FROM folders folder WHERE folder.Id = old.FolderId;
            END;
            CREATE TRIGGER catalog_search_tags_update AFTER UPDATE OF Name ON tags BEGIN
                UPDATE catalog_search SET tags = COALESCE((
                    SELECT group_concat(tag.Name, ' ') FROM file_tags assignment JOIN tags tag ON tag.Id = assignment.TagId
                    WHERE assignment.FileId = catalog_search.item_id), '')
                WHERE kind = 'file' AND item_id IN (SELECT FileId FROM file_tags WHERE TagId = new.Id);
                UPDATE catalog_search SET tags = COALESCE((
                    SELECT group_concat(tag.Name, ' ') FROM folder_tags assignment JOIN tags tag ON tag.Id = assignment.TagId
                    WHERE assignment.FolderId = catalog_search.item_id), '')
                WHERE kind = 'folder' AND item_id IN (SELECT FolderId FROM folder_tags WHERE TagId = new.Id);
            END;
            CREATE TRIGGER timeline_event_search_insert AFTER INSERT ON timeline_events BEGIN
                INSERT INTO timeline_event_search (event_id, library_id, title, description)
                VALUES (new.Id, new.LibraryId, new.Title, COALESCE(new.Description, ''));
            END;
            CREATE TRIGGER timeline_event_search_update AFTER UPDATE ON timeline_events BEGIN
                DELETE FROM timeline_event_search WHERE event_id = old.Id;
                INSERT INTO timeline_event_search (event_id, library_id, title, description)
                VALUES (new.Id, new.LibraryId, new.Title, COALESCE(new.Description, ''));
            END;
            CREATE TRIGGER timeline_event_search_delete AFTER DELETE ON timeline_events BEGIN
                DELETE FROM timeline_event_search WHERE event_id = old.Id;
            END;
            """,
            cancellationToken);
    }

    private async Task EnsureRelationalPrerequisitesAsync(CancellationToken cancellationToken)
    {
        string[] requiredTables = ["folders", "files", "tags", "file_tags", "folder_tags", "timeline_events"];
        var missingTables = new List<string>();

        foreach (var table in requiredTables)
        {
            if (!await SchemaObjectExistsAsync("table", table, cancellationToken))
            {
                missingTables.Add(table);
            }
        }

        if (missingTables.Count == 0) return;

        throw new InvalidOperationException(
            "The Media catalog relational schema is not current. Missing required table(s): " +
            $"{string.Join(", ", missingTables)}. Apply the Media EF Core migrations before starting the host.");
    }

    private async Task EnsureDerivedSchemaVersionTableAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS derived_schema_versions (
                Component TEXT NOT NULL PRIMARY KEY,
                Version INTEGER NOT NULL CHECK (Version >= 1),
                AppliedAt TEXT NOT NULL
            );
            """,
            cancellationToken);
    }

    private async Task RecordCatalogSearchSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO derived_schema_versions (Component, Version, AppliedAt)
            VALUES ('{CatalogSearchComponent}', {CatalogSearchSchemaVersion}, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            ON CONFLICT(Component) DO UPDATE SET
                Version = excluded.Version,
                AppliedAt = excluded.AppliedAt;
            """,
            cancellationToken);
    }

    private async Task<bool> IsCatalogSearchCurrentAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        var shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose) await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText =
                "SELECT Version FROM derived_schema_versions WHERE Component = $component;";
            var componentParameter = versionCommand.CreateParameter();
            componentParameter.ParameterName = "$component";
            componentParameter.Value = CatalogSearchComponent;
            versionCommand.Parameters.Add(componentParameter);

            var versionValue = await versionCommand.ExecuteScalarAsync(cancellationToken);
            if (versionValue is null || Convert.ToInt32(versionValue) != CatalogSearchSchemaVersion)
            {
                return false;
            }

            await using var objectsCommand = connection.CreateCommand();
            objectsCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE
                    (type = 'table'
                     AND name IN ('catalog_search', 'timeline_event_search')
                     AND lower(sql) LIKE '%using fts5%')
                    OR
                    (type = 'trigger' AND name IN (
                        'catalog_search_folders_insert',
                        'catalog_search_folders_update',
                        'catalog_search_folders_delete',
                        'catalog_search_files_insert',
                        'catalog_search_files_update',
                        'catalog_search_files_delete',
                        'catalog_search_file_tags_insert',
                        'catalog_search_file_tags_delete',
                        'catalog_search_folder_tags_insert',
                        'catalog_search_folder_tags_delete',
                        'catalog_search_tags_update',
                        'timeline_event_search_insert',
                        'timeline_event_search_update',
                        'timeline_event_search_delete'
                    ));
                """;

            var objectCount = Convert.ToInt32(await objectsCommand.ExecuteScalarAsync(cancellationToken));
            return objectCount == RequiredCatalogSearchObjectCount;
        }
        finally
        {
            if (shouldClose) await _dbContext.Database.CloseConnectionAsync();
        }
    }

    private async Task<bool> SchemaObjectExistsAsync(
        string type,
        string name,
        CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        var shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose) await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = $type AND name = $name);";

            var typeParameter = command.CreateParameter();
            typeParameter.ParameterName = "$type";
            typeParameter.Value = type;
            command.Parameters.Add(typeParameter);

            var nameParameter = command.CreateParameter();
            nameParameter.ParameterName = "$name";
            nameParameter.Value = name;
            command.Parameters.Add(nameParameter);

            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        }
        finally
        {
            if (shouldClose) await _dbContext.Database.CloseConnectionAsync();
        }
    }

    private async Task ApplyDatabasePragmasAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
    }
}
