using FluentAssertions;
using CSSApps.Shared.Infrastructure.Data.Turso;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class CatalogSchemaTests
{
    [Fact]
    public async Task Derived_search_schema_requires_current_relational_migrations()
    {
        await using var harness = new MediaTestHarness();
        Directory.CreateDirectory(harness.RootDirectory);
        await using var dbContext = harness.CreateDbContext();

        var action = () => new MediaCatalogInitializer(dbContext).EnsureDerivedSchemaAsync();

        await action.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Missing required table(s):*Apply the Media EF Core migrations*");
    }

    [Fact]
    public async Task Derived_search_schema_is_created_after_relational_migrations()
    {
        await using var harness = new MediaTestHarness();
        Directory.CreateDirectory(harness.RootDirectory);
        await using var dbContext = harness.CreateDbContext();
        await dbContext.Database.MigrateAsync();

        (await SchemaObjectExistsAsync(dbContext, "table", "catalog_search")).Should().BeFalse();

        await new MediaCatalogInitializer(dbContext).EnsureDerivedSchemaAsync();

        (await SchemaObjectExistsAsync(dbContext, "table", "catalog_search")).Should().BeTrue();
        (await SchemaObjectExistsAsync(dbContext, "table", "timeline_event_search")).Should().BeTrue();
        (await CatalogSearchVersionAsync(dbContext))
            .Should().Be(MediaCatalogInitializer.CatalogSearchSchemaVersion);
    }

    [Fact]
    public async Task Current_derived_search_schema_is_not_rebuilt_on_restart()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var dbContext = harness.CreateDbContext();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog_search (item_id, library_id, kind, name, tags)
            VALUES ('restart-marker', 'marker-library', 'file', 'restart marker', '');
            """);

        await new MediaCatalogInitializer(dbContext).EnsureDerivedSchemaAsync();

        (await CatalogSearchMarkerCountAsync(dbContext, "restart-marker"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Stale_derived_search_version_rebuilds_and_backfills_the_index()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var dbContext = harness.CreateDbContext();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog_search (item_id, library_id, kind, name, tags)
            VALUES ('stale-marker', 'marker-library', 'file', 'stale marker', '');
            UPDATE derived_schema_versions
            SET Version = 1
            WHERE Component = 'catalog_search';
            """);

        await new MediaCatalogInitializer(dbContext).EnsureDerivedSchemaAsync();

        (await CatalogSearchMarkerCountAsync(dbContext, "stale-marker"))
            .Should().Be(0);
        (await CatalogSearchVersionAsync(dbContext))
            .Should().Be(MediaCatalogInitializer.CatalogSearchSchemaVersion);
    }

    [Fact]
    public async Task Missing_search_table_is_rebuilt_even_when_version_is_current()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var dbContext = harness.CreateDbContext();
        await dbContext.Database.ExecuteSqlRawAsync("DROP TABLE timeline_event_search;");

        await new MediaCatalogInitializer(dbContext).EnsureDerivedSchemaAsync();

        (await SchemaObjectExistsAsync(dbContext, "table", "timeline_event_search")).Should().BeTrue();
    }

    [Fact]
    public async Task Search_folds_accents_and_case_and_follows_renames()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var userId = Guid.NewGuid();
        var folder = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Cumpleaños de JOSÉ" }, CancellationToken.None);

        (await SearchIdsAsync(service, userId, "jose cumpleanos")).Should().Equal(folder.Id);
        (await SearchIdsAsync(service, userId, "ÑOS")).Should().Equal(folder.Id);
        (await SearchIdsAsync(service, userId, "navidad")).Should().BeEmpty();

        await service.RenameItemAsync(userId, folder.Id, new RenameItemReq { Name = "Navidad" }, CancellationToken.None);

        (await SearchIdsAsync(service, userId, "jose")).Should().BeEmpty();
        (await SearchIdsAsync(service, userId, "navidad")).Should().Equal(folder.Id);
    }

    private static async Task<Guid[]> SearchIdsAsync(MediaTestClient service, Guid userId, string query) =>
        (await service.QueryFilesAsync(userId, new FileQueryOptions { Query = query, Location = "all", Limit = 50 }, CancellationToken.None))
        .Items.Select(item => item.Id).ToArray();

    [Fact]
    public async Task Replica_route_reads_but_rejects_every_write()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var options = new DbContextOptionsBuilder<MediaCatalogDbContext>();
        harness.Database.Configure(options, DatabaseRouteKind.Replica, TursoDatabase.MediaHistoryTable);
        await using var replica = new MediaCatalogDbContext(options.Options);

        (await replica.Libraries.CountAsync()).Should().Be(0);
        replica.Libraries.Add(new MediaExplorer.Media.Domain.Library { OwnerUserId = Guid.NewGuid() });
        (await replica.Invoking(context => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<ReplicaWriteException>();
        await replica.Invoking(context => context.Database.ExecuteSqlRawAsync("DELETE FROM tags;")).Should().ThrowAsync<ReplicaWriteException>();
    }

    [Fact]
    public async Task Catalog_hot_paths_have_the_expected_composite_indexes()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var dbContext = harness.CreateDbContext();
        await dbContext.Database.OpenConnectionAsync();

        // Turso reports index names in lowercase, so compare case-insensitively.
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) indexes.Add(reader.GetString(0).ToLowerInvariant());

        indexes.Should().Contain(((string[])[
            "IX_files_LibraryId_TrashedAt_CapturedAt_Sequence",
            "IX_files_LibraryId_TrashedAt_Sequence",
            "IX_files_LibraryId_TrashRootId",
            "IX_folders_LibraryId_TrashedAt_Sequence",
            "IX_folders_LibraryId_TrashRootId",
            "IX_file_version_metadata_State_ProcessingStartedAt_FileVersionId",
            "IX_file_version_metadata_State_ProcessedAt_FileVersionId",
            "IX_file_version_metadata_State_ExtractorVersion_FileVersionId",
            "IX_file_version_metadata_State_RecoveryManifestState_FileVersionId",
            "IX_upload_sessions_CatalogFileId_State_CompletedAt",
            "IX_import_items_ImportJobId_SourceRelativePath",
            "IX_import_items_State_ProcessingStartedAt_Id",
            "IX_import_jobs_UserId_CreatedAt",
            "IX_import_jobs_TargetFolderId",
            "IX_album_items_AlbumId_AddedAt_FileId",
            "IX_album_items_FileId",
            "IX_albums_LibraryId_ModifiedAt_Id",
            "IX_timeline_events_LibraryId_StartDate_Id",
            "IX_catalog_shares_AlbumId_RecipientUserId",
            "IX_tags_LibraryId_NormalizedName",
            "IX_tags_LibraryId_Name_Id",
            "IX_file_tags_TagId_FileId",
            "IX_folder_tags_TagId_FolderId"
        ]).Select(name => name.ToLowerInvariant()));

        indexes.Should().NotContain(((string[])[
            "IX_files_LibraryId_TrashedAt",
            "IX_folders_LibraryId_TrashedAt",
            "IX_files_LibraryId_CapturedAt_Sequence",
            "IX_upload_sessions_CatalogFileId",
            "IX_file_version_metadata_State_ProcessingStartedAt",
            "IX_file_version_metadata_State_ExtractorVersion",
            "IX_file_version_metadata_State_RecoveryManifestState"
        ]).Select(name => name.ToLowerInvariant()));
    }

    private static async Task<bool> SchemaObjectExistsAsync(
        MediaCatalogDbContext dbContext,
        string type,
        string name)
    {
        await dbContext.Database.OpenConnectionAsync();
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
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

            return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task<int> CatalogSearchVersionAsync(MediaCatalogDbContext dbContext)
    {
        await dbContext.Database.OpenConnectionAsync();
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                "SELECT Version FROM derived_schema_versions WHERE Component = 'catalog_search';";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task<int> CatalogSearchMarkerCountAsync(
        MediaCatalogDbContext dbContext,
        string itemId)
    {
        await dbContext.Database.OpenConnectionAsync();
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM catalog_search WHERE item_id = $itemId;";

            var itemIdParameter = command.CreateParameter();
            itemIdParameter.ParameterName = "$itemId";
            itemIdParameter.Value = itemId;
            command.Parameters.Add(itemIdParameter);

            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }
}
