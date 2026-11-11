using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Domain;
using CSSApps.Shared.Infrastructure.Data.Turso;
using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>Owns persistence-specific library bootstrap and destination lookup.</summary>
internal sealed class LibraryBootstrapper(MediaCatalogDbContext dbContext, DatabaseRoute? route = null, TursoDatabase? database = null)
{
    public const string MediaFolderRole = "media_root";
    public const string DocumentsFolderRole = "documents_root";

    public async Task<Library> GetOrCreateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var library = await dbContext.Libraries.SingleOrDefaultAsync(candidate => candidate.OwnerUserId == userId, cancellationToken);
        if (route?.IsReplica == true && database is not null)
            return library is not null && await HasSystemFoldersAsync(library.Id, cancellationToken)
                ? library
                : await CreateThroughCloudAsync(userId, cancellationToken);

        if (library is null)
        {
            library = new Library { OwnerUserId = userId };
            dbContext.Libraries.Add(library);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await EnsureSystemFoldersAsync(library, cancellationToken);
        return library;
    }

    // A first visit can be a read-only request on the replica. Create the library through a short cloud
    // connection instead (idempotent), then pull so this request reads it locally.
    private async Task<Library> CreateThroughCloudAsync(Guid userId, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<MediaCatalogDbContext>();
        database!.Configure(options, DatabaseRouteKind.Cloud, TursoDatabase.MediaHistoryTable);
        await using (var cloud = new MediaCatalogDbContext(options.Options))
            await new LibraryBootstrapper(cloud).GetOrCreateAsync(userId, cancellationToken);
        await database.PullAsync(cancellationToken);
        return await dbContext.Libraries.SingleAsync(candidate => candidate.OwnerUserId == userId, cancellationToken);
    }

    private async Task<bool> HasSystemFoldersAsync(Guid libraryId, CancellationToken cancellationToken) =>
        await dbContext.Folders.CountAsync(
            folder => folder.LibraryId == libraryId && (folder.SystemRole == MediaFolderRole || folder.SystemRole == DocumentsFolderRole),
            cancellationToken) == 2;

    public Task<Guid> SystemFolderIdAsync(Guid libraryId, string systemRole, CancellationToken cancellationToken) =>
        dbContext.Folders.AsNoTracking()
            .Where(folder => folder.LibraryId == libraryId && folder.SystemRole == systemRole)
            .Select(folder => folder.Id)
            .SingleAsync(cancellationToken);

    public async Task<Result> EnsureParentAsync(Guid libraryId, Guid? parentId, bool requireActive, CancellationToken cancellationToken)
    {
        if (!parentId.HasValue) return Result.Success();
        var parent = await dbContext.Folders.AsNoTracking()
            .SingleOrDefaultAsync(folder => folder.Id == parentId && folder.LibraryId == libraryId, cancellationToken);
        if (parent is null) return MediaErrors.NotFound("The destination folder was not found.");
        return requireActive ? CatalogItemPolicy.EnsureActive(parent.TrashedAt) : Result.Success();
    }

    private async Task EnsureSystemFoldersAsync(Library library, CancellationToken cancellationToken)
    {
        var existingRoles = await dbContext.Folders
            .Where(folder => folder.LibraryId == library.Id && folder.SystemRole != null)
            .Select(folder => folder.SystemRole!)
            .ToHashSetAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var added = false;
        if (!existingRoles.Contains(MediaFolderRole))
        {
            AddSystemFolder(library, "Media", MediaFolderRole, now);
            added = true;
        }
        if (!existingRoles.Contains(DocumentsFolderRole))
        {
            AddSystemFolder(library, "Documents", DocumentsFolderRole, now);
            added = true;
        }
        if (added) await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void AddSystemFolder(Library library, string name, string role, DateTime now) => dbContext.Folders.Add(new CatalogFolder
    {
        LibraryId = library.Id,
        Name = name,
        SystemRole = role,
        Sequence = ++library.NextItemSequence,
        CreatedAt = now,
        ModifiedAt = now
    });
}
