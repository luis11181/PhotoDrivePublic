using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>
/// Keeps <c>catalog_search</c> and <c>timeline_event_search</c> in step with the catalog. Every
/// <see cref="MediaCatalogDbContext"/> save collects the folders, files and events whose searchable text may have
/// changed, reads their current names and tags inside the same transaction, normalizes them with
/// <see cref="SearchText.Normalize"/>, and replaces exactly those rows. Rows left behind by database cascades are
/// harmless: every search query joins back to the live catalog row.
/// </summary>
internal static class CatalogSearchIndex
{
    private const int BatchSize = 200;

    internal sealed class PendingChanges
    {
        public HashSet<Guid> Files { get; } = [];
        public HashSet<Guid> Folders { get; } = [];
        public HashSet<Guid> Events { get; } = [];
        public bool IsEmpty => Files.Count == 0 && Folders.Count == 0 && Events.Count == 0;
    }

    /// <summary>Runs before EF writes, while tag assignments that a tag change affects are still readable.</summary>
    public static async Task<PendingChanges> CollectAsync(MediaCatalogDbContext dbContext, CancellationToken cancellationToken)
    {
        var pending = new PendingChanges();
        var changedTags = new HashSet<Guid>();
        foreach (var entry in dbContext.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached) continue;
            switch (entry.Entity)
            {
                case CatalogFile file when Changed(entry, nameof(CatalogFile.Name), nameof(CatalogFile.LibraryId)):
                    pending.Files.Add(file.Id);
                    break;
                case CatalogFolder folder when Changed(entry, nameof(CatalogFolder.Name), nameof(CatalogFolder.LibraryId)):
                    pending.Folders.Add(folder.Id);
                    break;
                case TimelineEvent timelineEvent when Changed(entry, nameof(TimelineEvent.Title), nameof(TimelineEvent.Description), nameof(TimelineEvent.LibraryId)):
                    pending.Events.Add(timelineEvent.Id);
                    break;
                case FileTag assignment when entry.State is EntityState.Added or EntityState.Deleted:
                    pending.Files.Add(assignment.FileId);
                    break;
                case FolderTag assignment when entry.State is EntityState.Added or EntityState.Deleted:
                    pending.Folders.Add(assignment.FolderId);
                    break;
                case Tag tag when entry.State == EntityState.Deleted || (entry.State == EntityState.Modified && entry.Property(nameof(Tag.Name)).IsModified):
                    changedTags.Add(tag.Id);
                    break;
            }
        }

        if (changedTags.Count > 0)
        {
            pending.Files.UnionWith(await dbContext.FileTags.AsNoTracking()
                .Where(assignment => changedTags.Contains(assignment.TagId))
                .Select(assignment => assignment.FileId)
                .ToListAsync(cancellationToken));
            pending.Folders.UnionWith(await dbContext.FolderTags.AsNoTracking()
                .Where(assignment => changedTags.Contains(assignment.TagId))
                .Select(assignment => assignment.FolderId)
                .ToListAsync(cancellationToken));
        }

        return pending;
    }

    /// <summary>Runs after EF writes, inside the same transaction, so derived rows commit with the change.</summary>
    public static async Task ApplyAsync(MediaCatalogDbContext dbContext, PendingChanges pending, CancellationToken cancellationToken)
    {
        foreach (var batch in pending.Files.Chunk(BatchSize)) await RefreshFilesAsync(dbContext, batch, cancellationToken);
        foreach (var batch in pending.Folders.Chunk(BatchSize)) await RefreshFoldersAsync(dbContext, batch, cancellationToken);
        foreach (var batch in pending.Events.Chunk(BatchSize)) await RefreshEventsAsync(dbContext, batch, cancellationToken);
    }

    /// <summary>Full rebuild used when the derived schema is created or its version changes.</summary>
    public static async Task RebuildAsync(MediaCatalogDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM catalog_search;", cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM timeline_event_search;", cancellationToken);
        foreach (var batch in (await dbContext.Files.AsNoTracking().Select(file => file.Id).ToListAsync(cancellationToken)).Chunk(BatchSize))
            await RefreshFilesAsync(dbContext, batch, cancellationToken);
        foreach (var batch in (await dbContext.Folders.AsNoTracking().Select(folder => folder.Id).ToListAsync(cancellationToken)).Chunk(BatchSize))
            await RefreshFoldersAsync(dbContext, batch, cancellationToken);
        foreach (var batch in (await dbContext.TimelineEvents.AsNoTracking().Select(item => item.Id).ToListAsync(cancellationToken)).Chunk(BatchSize))
            await RefreshEventsAsync(dbContext, batch, cancellationToken);
    }

    private static async Task RefreshFilesAsync(MediaCatalogDbContext dbContext, Guid[] ids, CancellationToken cancellationToken)
    {
        var files = await dbContext.Files.AsNoTracking()
            .Where(file => ids.Contains(file.Id))
            .Select(file => new { file.Id, file.LibraryId, file.Name })
            .ToListAsync(cancellationToken);
        var tags = await dbContext.FileTags.AsNoTracking()
            .Where(assignment => ids.Contains(assignment.FileId))
            .Join(dbContext.Tags, assignment => assignment.TagId, tag => tag.Id, (assignment, tag) => new { assignment.FileId, tag.Name })
            .ToListAsync(cancellationToken);
        var tagsByFile = tags.ToLookup(tag => tag.FileId, tag => tag.Name);
        await ReplaceCatalogRowsAsync(
            dbContext,
            "file",
            ids,
            files.Select(file => file.Id).ToList(),
            files.Select(file => new[] { file.Id.ToDbText(), "file", file.LibraryId.ToDbText(), SearchText.Normalize(file.Name), SearchText.Normalize(string.Join(' ', tagsByFile[file.Id])) }),
            cancellationToken);
    }

    private static async Task RefreshFoldersAsync(MediaCatalogDbContext dbContext, Guid[] ids, CancellationToken cancellationToken)
    {
        var folders = await dbContext.Folders.AsNoTracking()
            .Where(folder => ids.Contains(folder.Id))
            .Select(folder => new { folder.Id, folder.LibraryId, folder.Name })
            .ToListAsync(cancellationToken);
        var tags = await dbContext.FolderTags.AsNoTracking()
            .Where(assignment => ids.Contains(assignment.FolderId))
            .Join(dbContext.Tags, assignment => assignment.TagId, tag => tag.Id, (assignment, tag) => new { assignment.FolderId, tag.Name })
            .ToListAsync(cancellationToken);
        var tagsByFolder = tags.ToLookup(tag => tag.FolderId, tag => tag.Name);
        await ReplaceCatalogRowsAsync(
            dbContext,
            "folder",
            ids,
            folders.Select(folder => folder.Id).ToList(),
            folders.Select(folder => new[] { folder.Id.ToDbText(), "folder", folder.LibraryId.ToDbText(), SearchText.Normalize(folder.Name), SearchText.Normalize(string.Join(' ', tagsByFolder[folder.Id])) }),
            cancellationToken);
    }

    private static async Task RefreshEventsAsync(MediaCatalogDbContext dbContext, Guid[] ids, CancellationToken cancellationToken)
    {
        var events = await dbContext.TimelineEvents.AsNoTracking()
            .Where(item => ids.Contains(item.Id))
            .Select(item => new { item.Id, item.LibraryId, item.Title, item.Description })
            .ToListAsync(cancellationToken);
        var missing = ids.Except(events.Select(item => item.Id)).ToArray();
        if (missing.Length > 0)
            await ExecuteAsync(dbContext, $"DELETE FROM timeline_event_search WHERE event_id IN ({Placeholders(0, missing.Length)});", Values(missing), cancellationToken);
        await InsertAsync(
            dbContext,
            "INSERT OR REPLACE INTO timeline_event_search (event_id, library_id, title, description) VALUES ",
            events.Select(item => new[] { item.Id.ToDbText(), item.LibraryId.ToDbText(), SearchText.Normalize(item.Title), SearchText.Normalize(item.Description) }),
            cancellationToken);
    }

    // One upsert for items that still exist and a delete only for items that no longer do keeps a typical save
    // (rename, tag change) to a single extra statement.
    private static async Task ReplaceCatalogRowsAsync(MediaCatalogDbContext dbContext, string kind, Guid[] ids, IReadOnlyCollection<Guid> existing, IEnumerable<string[]> rows, CancellationToken cancellationToken)
    {
        var missing = ids.Except(existing).ToArray();
        if (missing.Length > 0)
        {
            var parameters = new List<object> { kind };
            parameters.AddRange(Values(missing));
            await ExecuteAsync(dbContext, $"DELETE FROM catalog_search WHERE kind = {{0}} AND item_id IN ({Placeholders(1, missing.Length)});", parameters.ToArray(), cancellationToken);
        }
        await InsertAsync(dbContext, "INSERT OR REPLACE INTO catalog_search (item_id, kind, library_id, name, tags) VALUES ", rows, cancellationToken);
    }

    private static async Task InsertAsync(MediaCatalogDbContext dbContext, string prefix, IEnumerable<string[]> rows, CancellationToken cancellationToken)
    {
        var materialized = rows.ToList();
        if (materialized.Count == 0) return;
        var width = materialized[0].Length;
        var values = materialized.SelectMany(row => row).Cast<object>().ToArray();
        var tuples = Enumerable.Range(0, materialized.Count).Select(row => $"({Placeholders(row * width, width)})");
        await ExecuteAsync(dbContext, prefix + string.Join(", ", tuples) + ";", values, cancellationToken);
    }

    private static bool Changed(EntityEntry entry, params string[] properties) =>
        entry.State is EntityState.Added or EntityState.Deleted ||
        (entry.State == EntityState.Modified && properties.Any(property => entry.Property(property).IsModified));

    private static string Placeholders(int start, int count) =>
        string.Join(", ", Enumerable.Range(start, count).Select(index => $"{{{index}}}"));

    private static object[] Values(Guid[] ids) => ids.Select(id => (object)id.ToDbText()).ToArray();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "EF1002:Risk of vulnerability to SQL injection", Justification = "Statements are compile-time templates; every value is bound as a parameter.")]
    private static Task ExecuteAsync(MediaCatalogDbContext dbContext, string sql, object[] parameters, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
}
