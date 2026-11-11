using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class CatalogAuthorization(MediaCatalogDbContext dbContext)
{
    public async Task<SharedCatalogIds> SharedItemIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roots = await dbContext.CatalogShares.AsNoTracking()
            .Where(share => share.RecipientUserId == userId && share.Role == Domain.CatalogShare.ViewerRole && share.FolderId.HasValue)
            .Select(share => new { share.LibraryId, FolderId = share.FolderId!.Value })
            .ToListAsync(cancellationToken);
        var folderIds = new HashSet<Guid>();
        foreach (var libraryRoots in roots.GroupBy(root => root.LibraryId))
        {
            var frontier = libraryRoots.Select(root => root.FolderId).Distinct().ToArray();
            foreach (var root in frontier) folderIds.Add(root);
            while (frontier.Length > 0)
            {
                var children = await dbContext.Folders.AsNoTracking()
                    .Where(folder => folder.LibraryId == libraryRoots.Key && folder.TrashedAt == null
                        && folder.ParentFolderId.HasValue && frontier.Contains(folder.ParentFolderId.Value))
                    .Select(folder => folder.Id).ToArrayAsync(cancellationToken);
                frontier = children.Where(folderIds.Add).ToArray();
            }
        }
        var directFileIds = await dbContext.CatalogShares.AsNoTracking()
            .Where(share => share.RecipientUserId == userId && share.Role == Domain.CatalogShare.ViewerRole && share.FileId.HasValue)
            .Select(share => share.FileId!.Value).ToListAsync(cancellationToken);
        var fileIds = directFileIds.ToHashSet();
        var albumFileIds = await (
            from membership in dbContext.AlbumItems.AsNoTracking()
            join album in dbContext.Albums.AsNoTracking() on membership.AlbumId equals album.Id
            join library in dbContext.Libraries.AsNoTracking() on album.LibraryId equals library.Id
            join file in dbContext.Files.AsNoTracking() on membership.FileId equals file.Id
            where file.TrashedAt == null && (library.OwnerUserId == userId
                || dbContext.CatalogShares.Any(share => share.AlbumId == album.Id && share.RecipientUserId == userId
                    && (share.Role == Domain.CatalogShare.ViewerRole || share.Role == Domain.CatalogShare.CollaboratorRole)))
            select membership.FileId).Distinct().ToListAsync(cancellationToken);
        fileIds.UnionWith(albumFileIds);
        if (folderIds.Count > 0)
        {
            var inherited = await dbContext.Files.AsNoTracking()
                .Where(file => file.TrashedAt == null && file.ParentFolderId.HasValue && folderIds.Contains(file.ParentFolderId.Value))
                .Select(file => file.Id).ToListAsync(cancellationToken);
            fileIds.UnionWith(inherited);
        }
        return new SharedCatalogIds(folderIds, fileIds);
    }

    public async Task<CatalogAccess?> FileAccessAsync(Guid userId, Guid fileId, bool requireActive, CancellationToken cancellationToken)
    {
        var item = await (
            from file in dbContext.Files.AsNoTracking()
            join library in dbContext.Libraries.AsNoTracking() on file.LibraryId equals library.Id
            where file.Id == fileId
            select new { file.LibraryId, file.ParentFolderId, file.TrashedAt, library.OwnerUserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null || requireActive && item.TrashedAt is not null) return null;
        if (item.OwnerUserId == userId) return new CatalogAccess(item.LibraryId, item.OwnerUserId, true);
        if (!requireActive) return null;

        var directlyShared = await dbContext.CatalogShares.AsNoTracking().AnyAsync(
            share => share.FileId == fileId && share.RecipientUserId == userId && share.Role == Domain.CatalogShare.ViewerRole,
            cancellationToken);
        var sharedThroughAlbum = await (
            from membership in dbContext.AlbumItems.AsNoTracking()
            join album in dbContext.Albums.AsNoTracking() on membership.AlbumId equals album.Id
            join library in dbContext.Libraries.AsNoTracking() on album.LibraryId equals library.Id
            where membership.FileId == fileId && (library.OwnerUserId == userId
                || dbContext.CatalogShares.Any(share => share.AlbumId == album.Id && share.RecipientUserId == userId
                    && (share.Role == Domain.CatalogShare.ViewerRole || share.Role == Domain.CatalogShare.CollaboratorRole)))
            select membership.FileId).AnyAsync(cancellationToken);
        if (directlyShared || sharedThroughAlbum || await HasFolderGrantAsync(userId, item.LibraryId, item.ParentFolderId, cancellationToken))
            return new CatalogAccess(item.LibraryId, item.OwnerUserId, false);
        return null;
    }

    public async Task<CatalogAccess?> FolderAccessAsync(Guid userId, Guid folderId, bool requireActive, CancellationToken cancellationToken)
    {
        var item = await (
            from folder in dbContext.Folders.AsNoTracking()
            join library in dbContext.Libraries.AsNoTracking() on folder.LibraryId equals library.Id
            where folder.Id == folderId
            select new { folder.LibraryId, folder.ParentFolderId, folder.TrashedAt, library.OwnerUserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null || requireActive && item.TrashedAt is not null) return null;
        if (item.OwnerUserId == userId) return new CatalogAccess(item.LibraryId, item.OwnerUserId, true);
        if (!requireActive) return null;

        if (await HasFolderGrantAsync(userId, item.LibraryId, folderId, cancellationToken))
            return new CatalogAccess(item.LibraryId, item.OwnerUserId, false);
        return null;
    }

    public async Task<bool> HasFolderGrantAsync(Guid userId, Guid libraryId, Guid? folderId, CancellationToken cancellationToken)
    {
        var current = folderId;
        while (current.HasValue)
        {
            var folder = current.Value;
            if (await dbContext.CatalogShares.AsNoTracking().AnyAsync(
                    share => share.LibraryId == libraryId && share.FolderId == folder
                        && share.RecipientUserId == userId && share.Role == Domain.CatalogShare.ViewerRole,
                    cancellationToken))
                return true;
            current = await dbContext.Folders.AsNoTracking()
                .Where(candidate => candidate.Id == folder && candidate.LibraryId == libraryId)
                .Select(candidate => candidate.ParentFolderId)
                .SingleOrDefaultAsync(cancellationToken);
        }
        return false;
    }
}

internal sealed record CatalogAccess(Guid LibraryId, Guid OwnerUserId, bool IsOwner);
internal sealed record SharedCatalogIds(HashSet<Guid> FolderIds, HashSet<Guid> FileIds);
