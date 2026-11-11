using System.Data;
using System.Globalization;
using System.Text;
using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class SharingService(
    MediaCatalogDbContext dbContext,
    CatalogAuthorization authorization,
    IShareRecipientDirectory recipientDirectory,
    ILogger<SharingService> logger) : ISharingService
{
    public async Task<Result<BrowseItemsRes>> BrowseSharedAsync(Guid userId, Guid? parentId, string? cursor, int limit, string type, string sort, CancellationToken cancellationToken)
    {
        var normalizedType = NormalizeType(type);
        var normalizedSort = NormalizeSort(sort);
        if (normalizedType is null) return MediaErrors.Validation("File type must be all, folder, document, pdf, spreadsheet, image, or video.");
        if (normalizedSort is null) return MediaErrors.Validation("Item sort must be name_asc, name_desc, modified_desc, modified_asc, size_desc, or size_asc.");
        var offset = DecodeCursor(cursor, normalizedSort);

        List<SharedEntry> entries;
        if (parentId.HasValue)
        {
            var access = await authorization.FolderAccessAsync(userId, parentId.Value, requireActive: true, cancellationToken);
            if (access is null || access.IsOwner) return MediaErrors.NotFound("The shared folder was not found.");
            entries = await ChildEntriesAsync(userId, access, parentId.Value, normalizedType, cancellationToken);
        }
        else
        {
            entries = await SharedRootsAsync(userId, normalizedType, cancellationToken);
        }

        entries = Sort(entries, normalizedSort).ToList();
        var page = entries.Skip(offset).Take(limit + 1).ToList();
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var ownerIds = page.Select(entry => entry.OwnerUserId).Distinct().ToArray();
        var owners = await recipientDirectory.FindByIdsAsync(ownerIds, cancellationToken);
        var folderIds = page.Where(entry => entry.IsFolder).Select(entry => entry.Id).ToArray();
        var fileIds = page.Where(entry => !entry.IsFolder).Select(entry => entry.Id).ToArray();
        var folders = await dbContext.Folders.AsNoTracking().Where(folder => folderIds.Contains(folder.Id)).ToDictionaryAsync(folder => folder.Id, cancellationToken);
        var files = await dbContext.Files.AsNoTracking().Where(file => fileIds.Contains(file.Id)).ToDictionaryAsync(file => file.Id, cancellationToken);
        var folderFavorites = await dbContext.FolderUserStates.AsNoTracking()
            .Where(state => state.UserId == userId && state.Favorite && folderIds.Contains(state.FolderId))
            .Select(state => state.FolderId).ToHashSetAsync(cancellationToken);
        var fileFavorites = await dbContext.FileUserStates.AsNoTracking()
            .Where(state => state.UserId == userId && state.Favorite && fileIds.Contains(state.FileId))
            .Select(state => state.FileId).ToHashSetAsync(cancellationToken);
        var items = page.Select(entry =>
        {
            owners.TryGetValue(entry.OwnerUserId, out var owner);
            return entry.IsFolder
                ? MediaCatalogService.ToFolderResponse(folders[entry.Id], folderFavorites.Contains(entry.Id), readOnly: true, sharedByDisplayName: owner?.DisplayName, sharedAt: entry.SharedAt)
                : MediaCatalogService.ToFileResponse(files[entry.Id], fileFavorites.Contains(entry.Id), readOnly: true, sharedByDisplayName: owner?.DisplayName, sharedAt: entry.SharedAt);
        }).ToList();

        logger.LogInformation("[media.sharing] shared browse completed parentId={ParentId} items={ItemCount} hasMore={HasMore}", parentId, items.Count, hasMore);
        return new BrowseItemsRes
        {
            Items = items,
            NextCursor = hasMore ? EncodeCursor(normalizedSort, offset + page.Count) : null
        };
    }

    public async Task<Result<BrowseItemsRes>> SearchSharedAsync(Guid userId, string query, string type, string? cursor, int limit, Guid? tagId, CancellationToken cancellationToken)
    {
        var searchTerms = SearchText.Terms(query);
        if (searchTerms is null) return MediaErrors.Validation("Enter at least two letters or numbers to search shared items.");
        var normalizedType = NormalizeType(type);
        if (normalizedType is null) return MediaErrors.Validation("File type must be all, folder, document, pdf, spreadsheet, image, or video.");
        if (tagId.HasValue) return MediaErrors.Validation("Private library tags cannot filter shared items.");

        var offset = DecodeSearchCursor(cursor);
        var candidates = await SharedSearchCandidatesAsync(userId, searchTerms, normalizedType, offset, limit, cancellationToken);
        if (candidates.Count == 0) return new BrowseItemsRes();

        var pageCandidates = candidates.Take(limit).ToArray();
        var candidateIds = pageCandidates.Select(candidate => candidate.Id).ToArray();
        var folders = await dbContext.Folders.AsNoTracking()
            .Where(folder => candidateIds.Contains(folder.Id) && folder.TrashedAt == null)
            .ToDictionaryAsync(folder => folder.Id, cancellationToken);
        var files = await dbContext.Files.AsNoTracking()
            .Where(file => candidateIds.Contains(file.Id) && file.TrashedAt == null)
            .ToDictionaryAsync(file => file.Id, cancellationToken);
        var folderIds = folders.Keys.ToArray();
        var fileIds = files.Keys.ToArray();
        var folderFavorites = await dbContext.FolderUserStates.AsNoTracking()
            .Where(state => state.UserId == userId && state.Favorite && folderIds.Contains(state.FolderId))
            .Select(state => state.FolderId).ToHashSetAsync(cancellationToken);
        var fileFavorites = await dbContext.FileUserStates.AsNoTracking()
            .Where(state => state.UserId == userId && state.Favorite && fileIds.Contains(state.FileId))
            .Select(state => state.FileId).ToHashSetAsync(cancellationToken);
        var ownerIds = pageCandidates.Select(candidate => candidate.OwnerUserId).Distinct().ToArray();
        var owners = await recipientDirectory.FindByIdsAsync(ownerIds, cancellationToken);

        var items = pageCandidates.Select(candidate =>
        {
            owners.TryGetValue(candidate.OwnerUserId, out var owner);
            if (candidate.Kind == "folder" && folders.TryGetValue(candidate.Id, out var folder))
                return MediaCatalogService.ToFolderResponse(folder, folderFavorites.Contains(folder.Id), readOnly: true, sharedByDisplayName: owner?.DisplayName, sharedAt: candidate.SharedAt);
            return candidate.Kind == "file" && files.TryGetValue(candidate.Id, out var file)
                ? MediaCatalogService.ToFileResponse(file, fileFavorites.Contains(file.Id), readOnly: true, sharedByDisplayName: owner?.DisplayName, sharedAt: candidate.SharedAt)
                : null;
        }).Where(item => item is not null).Cast<MediaItemRes>().ToList();

        var hasMore = candidates.Count > limit;
        logger.LogInformation("[media.sharing] shared search completed items={ItemCount} hasMore={HasMore}", items.Count, hasMore);
        return new BrowseItemsRes
        {
            Items = items,
            NextCursor = hasMore ? EncodeSearchCursor(offset + pageCandidates.Length) : null
        };
    }

    public async Task<Result<ShareRecipientsRes>> ListRecipientsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var recipients = await recipientDirectory.ListEligibleAsync(userId, cancellationToken);
        return new ShareRecipientsRes
        {
            Recipients = recipients.Select(recipient => new ShareRecipientRes
            {
                UserId = recipient.UserId,
                DisplayName = recipient.DisplayName,
                Email = recipient.Email
            }).ToList()
        };
    }

    public async Task<Result<ItemSharesRes>> ListItemSharesAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var owned = await OwnedItemAsync(userId, itemId, cancellationToken);
        if (owned is null) return MediaErrors.NotFound("The requested item was not found in your library.");
        var shares = await dbContext.CatalogShares.AsNoTracking()
            .Where(share => owned.IsFolder ? share.FolderId == itemId : share.FileId == itemId)
            .OrderBy(share => share.CreatedAt)
            .ToListAsync(cancellationToken);
        var profiles = await recipientDirectory.FindByIdsAsync(shares.Select(share => share.RecipientUserId).Distinct().ToArray(), cancellationToken);
        return new ItemSharesRes
        {
            ItemId = itemId,
            Shares = shares.Select(share =>
            {
                profiles.TryGetValue(share.RecipientUserId, out var profile);
                return new ItemShareRes
                {
                    RecipientUserId = share.RecipientUserId,
                    RecipientDisplayName = profile?.DisplayName ?? "Family member",
                    RecipientEmail = profile?.Email ?? string.Empty,
                    Role = share.Role,
                    SharedAt = share.CreatedAt
                };
            }).ToList()
        };
    }

    public async Task<Result<ItemShareRes>> SetItemShareAsync(Guid userId, Guid itemId, Guid recipientUserId, SetItemShareReq request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Role?.Trim(), CatalogShare.ViewerRole, StringComparison.OrdinalIgnoreCase))
            return MediaErrors.Validation("Sharing role must be viewer.");
        if (recipientUserId == userId) return MediaErrors.Validation("You cannot share an item with yourself.");
        var owned = await OwnedItemAsync(userId, itemId, cancellationToken);
        if (owned is null) return MediaErrors.NotFound("The requested item was not found in your library.");
        if (owned.IsSystemFolder) return MediaErrors.Conflict("Protected Media and Documents folders cannot be shared.");
        if (owned.TrashedAt is not null) return MediaErrors.Conflict("Restore the item before sharing it.");
        var recipient = await recipientDirectory.FindEligibleAsync(recipientUserId, cancellationToken);
        if (recipient is null) return MediaErrors.Validation("Choose an available family member.");

        var share = await dbContext.CatalogShares.SingleOrDefaultAsync(candidate =>
            candidate.RecipientUserId == recipientUserId
            && (owned.IsFolder ? candidate.FolderId == itemId : candidate.FileId == itemId), cancellationToken);
        var now = DateTime.UtcNow;
        if (share is null)
        {
            share = new CatalogShare
            {
                LibraryId = owned.LibraryId,
                FolderId = owned.IsFolder ? itemId : null,
                FileId = owned.IsFolder ? null : itemId,
                RecipientUserId = recipientUserId,
                GrantedByUserId = userId,
                Role = CatalogShare.ViewerRole,
                CreatedAt = now,
                ModifiedAt = now
            };
            dbContext.CatalogShares.Add(share);
        }
        else
        {
            share.Role = CatalogShare.ViewerRole;
            share.GrantedByUserId = userId;
            share.ModifiedAt = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.sharing] grant saved itemId={ItemId} recipientUserId={RecipientUserId} role={Role}", itemId, recipientUserId, share.Role);
        return new ItemShareRes
        {
            RecipientUserId = recipient.UserId,
            RecipientDisplayName = recipient.DisplayName,
            RecipientEmail = recipient.Email,
            Role = share.Role,
            SharedAt = share.CreatedAt
        };
    }

    public async Task<Result> RevokeItemShareAsync(Guid userId, Guid itemId, Guid recipientUserId, CancellationToken cancellationToken)
    {
        var owned = await OwnedItemAsync(userId, itemId, cancellationToken);
        if (owned is null) return MediaErrors.NotFound("The requested item was not found in your library.");
        var share = await dbContext.CatalogShares.SingleOrDefaultAsync(candidate =>
            candidate.RecipientUserId == recipientUserId
            && (owned.IsFolder ? candidate.FolderId == itemId : candidate.FileId == itemId), cancellationToken);
        if (share is null) return MediaErrors.NotFound("The share was not found.");
        dbContext.CatalogShares.Remove(share);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.sharing] grant revoked itemId={ItemId} recipientUserId={RecipientUserId}", itemId, recipientUserId);
        return Result.Success();
    }

    private async Task<List<SharedEntry>> SharedRootsAsync(Guid userId, string type, CancellationToken cancellationToken)
    {
        var folderRows = type is "all" or "folder"
            ? await (from share in dbContext.CatalogShares.AsNoTracking()
                     join folder in dbContext.Folders.AsNoTracking() on share.FolderId equals folder.Id
                     join library in dbContext.Libraries.AsNoTracking() on share.LibraryId equals library.Id
                     where share.RecipientUserId == userId && share.Role == CatalogShare.ViewerRole && folder.TrashedAt == null
                     select new SharedEntry(folder.Id, true, folder.Name, folder.ModifiedAt, null, library.OwnerUserId, share.CreatedAt)).ToListAsync(cancellationToken)
            : [];
        var fileRows = await (from share in dbContext.CatalogShares.AsNoTracking()
                              join file in dbContext.Files.AsNoTracking() on share.FileId equals file.Id
                              join library in dbContext.Libraries.AsNoTracking() on share.LibraryId equals library.Id
                              where share.RecipientUserId == userId && share.Role == CatalogShare.ViewerRole && file.TrashedAt == null
                              select new { file, library.OwnerUserId, share.CreatedAt }).ToListAsync(cancellationToken);
        folderRows.AddRange(fileRows.Where(row => MatchesType(row.file, type)).Select(row =>
            new SharedEntry(row.file.Id, false, row.file.Name, row.file.ModifiedAt, row.file.SizeBytes, row.OwnerUserId, row.CreatedAt)));
        return folderRows;
    }

    private async Task<List<SharedEntry>> ChildEntriesAsync(Guid userId, CatalogAccess access, Guid parentId, string type, CancellationToken cancellationToken)
    {
        var folders = type is "all" or "folder"
            ? await dbContext.Folders.AsNoTracking()
                .Where(folder => folder.LibraryId == access.LibraryId && folder.ParentFolderId == parentId && folder.TrashedAt == null)
                .Select(folder => new SharedEntry(folder.Id, true, folder.Name, folder.ModifiedAt, null, access.OwnerUserId, null))
                .ToListAsync(cancellationToken)
            : [];
        var files = await dbContext.Files.AsNoTracking()
            .Where(file => file.LibraryId == access.LibraryId && file.ParentFolderId == parentId && file.TrashedAt == null)
            .ToListAsync(cancellationToken);
        folders.AddRange(files.Where(file => MatchesType(file, type)).Select(file =>
            new SharedEntry(file.Id, false, file.Name, file.ModifiedAt, file.SizeBytes, access.OwnerUserId, null)));
        return folders;
    }

    private async Task<List<SharedSearchCandidate>> SharedSearchCandidatesAsync(Guid userId, IReadOnlyList<string> terms, string type, int offset, int limit, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            // Shared search matches names only: owner tags stay private to the owning library.
            var match = SearchText.MatchAll(command, terms, "q", "catalog_search.name");
            command.CommandText = """
                WITH RECURSIVE shared_folder_paths(item_id, library_id, owner_user_id, shared_at) AS (
                    SELECT folder.Id, folder.LibraryId, library.OwnerUserId, share.CreatedAt
                    FROM catalog_shares share
                    JOIN folders folder ON folder.Id = share.FolderId
                    JOIN libraries library ON library.Id = folder.LibraryId
                    WHERE share.RecipientUserId = $userId COLLATE NOCASE
                      AND share.Role = $role
                      AND folder.TrashedAt IS NULL
                    UNION ALL
                    SELECT child.Id, child.LibraryId, parent.owner_user_id, parent.shared_at
                    FROM folders child
                    JOIN shared_folder_paths parent
                      ON child.ParentFolderId = parent.item_id
                     AND child.LibraryId = parent.library_id
                    WHERE child.TrashedAt IS NULL
                ),
                shared_folders AS (
                    SELECT item_id, library_id, owner_user_id, MIN(shared_at) AS shared_at
                    FROM shared_folder_paths
                    GROUP BY item_id, library_id, owner_user_id
                ),
                shared_file_paths(item_id, library_id, owner_user_id, shared_at) AS (
                    SELECT file.Id, file.LibraryId, library.OwnerUserId, share.CreatedAt
                    FROM catalog_shares share
                    JOIN files file ON file.Id = share.FileId
                    JOIN libraries library ON library.Id = file.LibraryId
                    WHERE share.RecipientUserId = $userId COLLATE NOCASE
                      AND share.Role = $role
                      AND file.TrashedAt IS NULL
                    UNION ALL
                    SELECT file.Id, file.LibraryId, parent.owner_user_id, parent.shared_at
                    FROM files file
                    JOIN shared_folders parent
                      ON file.ParentFolderId = parent.item_id
                     AND file.LibraryId = parent.library_id
                    WHERE file.TrashedAt IS NULL
                ),
                shared_files AS (
                    SELECT item_id, library_id, owner_user_id, MIN(shared_at) AS shared_at
                    FROM shared_file_paths
                    GROUP BY item_id, library_id, owner_user_id
                )
                SELECT catalog_search.item_id,
                       catalog_search.kind,
                       COALESCE(shared_folders.owner_user_id, shared_files.owner_user_id),
                       COALESCE(shared_folders.shared_at, shared_files.shared_at)
                FROM catalog_search
                LEFT JOIN shared_folders
                  ON catalog_search.kind = 'folder'
                 AND catalog_search.item_id = shared_folders.item_id COLLATE NOCASE
                 AND catalog_search.library_id = shared_folders.library_id COLLATE NOCASE
                LEFT JOIN shared_files
                  ON catalog_search.kind = 'file'
                 AND catalog_search.item_id = shared_files.item_id COLLATE NOCASE
                 AND catalog_search.library_id = shared_files.library_id COLLATE NOCASE
                WHERE {match}
                  AND (
                    (catalog_search.kind = 'folder'
                      AND shared_folders.item_id IS NOT NULL
                      AND $type IN ('all', 'folder'))
                    OR
                    (catalog_search.kind = 'file'
                      AND shared_files.item_id IS NOT NULL
                      AND $type <> 'folder'
                      AND EXISTS (
                        SELECT 1 FROM files file
                        WHERE file.Id = catalog_search.item_id COLLATE NOCASE
                          AND (
                            $type = 'all'
                            OR ($type = 'document' AND (
                              file.MimeType LIKE 'text/%'
                              OR file.MimeType IN ('application/msword', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'application/vnd.oasis.opendocument.text', 'application/rtf')
                            ))
                            OR ($type = 'pdf' AND (file.MimeType = 'application/pdf' OR lower(file.Name) LIKE '%.pdf'))
                            OR ($type = 'spreadsheet' AND file.MimeType IN ('text/csv', 'application/vnd.ms-excel', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', 'application/vnd.oasis.opendocument.spreadsheet'))
                            OR ($type = 'image' AND file.MimeType LIKE 'image/%')
                            OR ($type = 'video' AND file.MimeType LIKE 'video/%')
                          )
                      ))
                  )
                ORDER BY catalog_search.name, catalog_search.item_id
                LIMIT $limit OFFSET $offset;
                """.Replace("{match}", match, StringComparison.Ordinal);
            AddParameter(command, "$userId", userId.ToDbText());
            AddParameter(command, "$role", CatalogShare.ViewerRole);
            AddParameter(command, "$type", type);
            AddParameter(command, "$limit", limit + 1);
            AddParameter(command, "$offset", offset);

            var candidates = new List<SharedSearchCandidate>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (Guid.TryParse(reader.GetString(0), out var id)
                    && Guid.TryParse(reader.GetString(2), out var ownerUserId))
                {
                    candidates.Add(new SharedSearchCandidate(
                        id,
                        reader.GetString(1),
                        ownerUserId,
                        DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
                }
            }
            return candidates;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task<OwnedItem?> OwnedItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var folder = await (from candidate in dbContext.Folders.AsNoTracking()
                            join library in dbContext.Libraries.AsNoTracking() on candidate.LibraryId equals library.Id
                            where candidate.Id == itemId && library.OwnerUserId == userId
                            select new OwnedItem(candidate.LibraryId, true, candidate.SystemRole != null, candidate.TrashedAt)).SingleOrDefaultAsync(cancellationToken);
        if (folder is not null) return folder;
        return await (from candidate in dbContext.Files.AsNoTracking()
                      join library in dbContext.Libraries.AsNoTracking() on candidate.LibraryId equals library.Id
                      where candidate.Id == itemId && library.OwnerUserId == userId
                      select new OwnedItem(candidate.LibraryId, false, false, candidate.TrashedAt)).SingleOrDefaultAsync(cancellationToken);
    }

    private static IEnumerable<SharedEntry> Sort(IEnumerable<SharedEntry> entries, string sort)
    {
        var foldersFirst = entries.OrderByDescending(entry => entry.IsFolder);
        return sort switch
        {
            "name_desc" => foldersFirst.ThenByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Id),
            "modified_desc" => foldersFirst.ThenByDescending(entry => entry.ModifiedAt).ThenBy(entry => entry.Id),
            "modified_asc" => foldersFirst.ThenBy(entry => entry.ModifiedAt).ThenBy(entry => entry.Id),
            "size_desc" => foldersFirst.ThenByDescending(entry => entry.SizeBytes ?? 0).ThenBy(entry => entry.Id),
            "size_asc" => foldersFirst.ThenBy(entry => entry.SizeBytes ?? 0).ThenBy(entry => entry.Id),
            _ => foldersFirst.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Id)
        };
    }

    private static bool MatchesType(CatalogFile file, string type) => type switch
    {
        "all" => true,
        "folder" => false,
        "image" => file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
        "video" => file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase),
        "pdf" => file.MimeType == "application/pdf" || file.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase),
        "spreadsheet" => file.MimeType is "text/csv" or "application/vnd.ms-excel" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or "application/vnd.oasis.opendocument.spreadsheet",
        "document" => file.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || file.MimeType is "application/msword" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document" or "application/vnd.oasis.opendocument.text" or "application/rtf",
        _ => false
    };

    private static string? NormalizeType(string value) => string.IsNullOrWhiteSpace(value) ? "all" : value.Trim().ToLowerInvariant() is var normalized && normalized is "all" or "folder" or "document" or "pdf" or "spreadsheet" or "image" or "video" ? normalized : null;
    private static string? NormalizeSort(string value) => string.IsNullOrWhiteSpace(value) ? "name_asc" : value.Trim().ToLowerInvariant() is var normalized && normalized is "name_asc" or "name_desc" or "modified_desc" or "modified_asc" or "size_desc" or "size_asc" ? normalized : null;
    private static string EncodeCursor(string sort, int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sort}:{offset}"));
    private static int DecodeCursor(string? cursor, string sort)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return decoded.StartsWith($"{sort}:", StringComparison.Ordinal) && int.TryParse(decoded[(sort.Length + 1)..], out var offset) && offset >= 0 ? offset : 0;
        }
        catch (FormatException) { return 0; }
    }


    private static int DecodeSearchCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(value, out var offset) && offset >= 0 ? offset : 0;
        }
        catch (FormatException) { return 0; }
    }

    private static string EncodeSearchCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private sealed record SharedEntry(Guid Id, bool IsFolder, string Name, DateTime ModifiedAt, long? SizeBytes, Guid OwnerUserId, DateTime? SharedAt);
    private sealed record SharedSearchCandidate(Guid Id, string Kind, Guid OwnerUserId, DateTime SharedAt);
    private sealed record OwnedItem(Guid LibraryId, bool IsFolder, bool IsSystemFolder, DateTime? TrashedAt);
}
