using System.Text;
using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class AlbumService(
    MediaCatalogDbContext dbContext,
    LibraryBootstrapper libraryBootstrapper,
    IShareRecipientDirectory recipientDirectory,
    ILogger<AlbumService> logger) : IAlbumService
{
    private const int MaximumBatchItems = 300;

    public async Task<Result<AlbumsRes>> ListAsync(Guid userId, string scope, CancellationToken cancellationToken)
    {
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim().ToLowerInvariant();
        if (normalizedScope is not ("all" or "owned" or "shared"))
            return MediaErrors.Validation("Album scope must be all, owned, or shared.");

        var rows = new List<AlbumRow>();
        if (normalizedScope is "all" or "owned")
        {
            rows.AddRange(await (
                from album in dbContext.Albums.AsNoTracking()
                join library in dbContext.Libraries.AsNoTracking() on album.LibraryId equals library.Id
                where library.OwnerUserId == userId
                select new AlbumRow(album, library.OwnerUserId, true, null)).ToListAsync(cancellationToken));
        }
        if (normalizedScope is "all" or "shared")
        {
            rows.AddRange(await (
                from share in dbContext.CatalogShares.AsNoTracking()
                join album in dbContext.Albums.AsNoTracking() on share.AlbumId equals album.Id
                join library in dbContext.Libraries.AsNoTracking() on album.LibraryId equals library.Id
                where share.RecipientUserId == userId
                    && (share.Role == CatalogShare.ViewerRole || share.Role == CatalogShare.CollaboratorRole)
                select new AlbumRow(album, library.OwnerUserId, false, share.CreatedAt, share.Role)).ToListAsync(cancellationToken));
        }

        var albumIds = rows.Select(row => row.Album.Id).Distinct().ToArray();
        var facts = await MemberFactsAsync(albumIds, cancellationToken);
        var ownerProfiles = await recipientDirectory.FindByIdsAsync(
            rows.Where(row => !row.IsOwner).Select(row => row.OwnerUserId).Distinct().ToArray(), cancellationToken);
        return new AlbumsRes
        {
            Albums = rows
                .OrderByDescending(row => row.Album.ModifiedAt)
                .ThenBy(row => row.Album.Name)
                .ThenBy(row => row.Album.Id)
                .Select(row => ToAlbumResponse(row, facts.GetValueOrDefault(row.Album.Id), ownerProfiles.GetValueOrDefault(row.OwnerUserId)))
                .ToList()
        };
    }

    public async Task<Result<AlbumDetailsRes>> GetAsync(Guid userId, Guid albumId, BrowseAlbumItemsReq request, CancellationToken cancellationToken)
    {
        var access = await AlbumAccessAsync(userId, albumId, cancellationToken);
        if (access is null) return MediaErrors.NotFound("The album was not found.");
        var offset = DecodeCursor(request.Cursor);
        if (offset is null) return MediaErrors.Validation("The album cursor is invalid.");
        var normalizedType = string.IsNullOrWhiteSpace(request.Type) ? "all" : request.Type.Trim().ToLowerInvariant();
        if (normalizedType is not ("all" or "image" or "video"))
            return MediaErrors.Validation("Album media type must be all, image, or video.");
        var normalizedSort = string.IsNullOrWhiteSpace(request.Sort) ? "added_desc" : request.Sort.Trim().ToLowerInvariant();
        if (normalizedSort is not ("captured_desc" or "captured_asc" or "added_desc"))
            return MediaErrors.Validation("Album sort must be captured_desc, captured_asc, or added_desc.");
        if (request.Year is < 1 or > 9999)
            return MediaErrors.Validation("Album year must be between 1 and 9999.");
        if (request.TagId.HasValue && (!access.IsOwner || !await dbContext.Tags.AsNoTracking()
                .AnyAsync(tag => tag.Id == request.TagId && tag.LibraryId == access.Album.LibraryId, cancellationToken)))
            return MediaErrors.NotFound("The requested tag was not found in this library.");

        var members =
            from membership in dbContext.AlbumItems.AsNoTracking()
            join file in dbContext.Files.AsNoTracking() on membership.FileId equals file.Id
            where membership.AlbumId == albumId && file.TrashedAt == null
            select new { File = file, membership.AddedAt };
        if (normalizedType == "image") members = members.Where(member => member.File.MimeType.StartsWith("image/"));
        if (normalizedType == "video") members = members.Where(member => member.File.MimeType.StartsWith("video/"));
        if (request.Favorite)
            members = members.Where(member => dbContext.FileUserStates.Any(state =>
                state.UserId == userId && state.FileId == member.File.Id && state.Favorite));
        if (request.TagId.HasValue)
            members = members.Where(member => dbContext.FileTags.Any(assignment =>
                assignment.FileId == member.File.Id && assignment.TagId == request.TagId));
        var normalizedQuery = request.Query?.Trim();
        if (normalizedQuery?.Length > 200)
            return MediaErrors.Validation("Album search must be 200 characters or fewer.");
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var pattern = $"%{EscapeLikePattern(normalizedQuery)}%";
            members = members.Where(member =>
                EF.Functions.Like(member.File.Name, pattern, "\\") ||
                (access.IsOwner && dbContext.FileTags.Any(assignment =>
                    assignment.FileId == member.File.Id && dbContext.Tags.Any(tag =>
                        tag.Id == assignment.TagId && tag.LibraryId == access.Album.LibraryId
                        && EF.Functions.Like(tag.Name, pattern, "\\")))));
        }

        var availableYears = await members
            .Select(member => member.File.CapturedAt.Year)
            .Distinct()
            .OrderByDescending(year => year)
            .ToListAsync(cancellationToken);
        if (request.Year.HasValue)
        {
            var yearStart = new DateTime(request.Year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            members = members.Where(member => member.File.CapturedAt >= yearStart && member.File.CapturedAt < yearStart.AddYears(1));
        }

        var orderedMembers = normalizedSort switch
        {
            "captured_desc" => members.OrderByDescending(member => member.File.CapturedAt).ThenByDescending(member => member.File.Sequence),
            "captured_asc" => members.OrderBy(member => member.File.CapturedAt).ThenBy(member => member.File.Sequence),
            _ => members.OrderByDescending(member => member.AddedAt).ThenBy(member => member.File.Id)
        };
        var limit = Math.Clamp(request.Limit, 1, 100);
        var page = await orderedMembers.Skip(offset.Value).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var files = page.Select(member => member.File).ToList();
        var fileIds = files.Select(file => file.Id).ToArray();
        var favorites = await dbContext.FileUserStates.AsNoTracking()
            .Where(state => state.UserId == userId && state.Favorite && fileIds.Contains(state.FileId))
            .Select(state => state.FileId).ToHashSetAsync(cancellationToken);
        var ownedLibraryIds = await dbContext.Libraries.AsNoTracking()
            .Where(library => library.OwnerUserId == userId)
            .Select(library => library.Id).ToArrayAsync(cancellationToken);
        var tags = await TagsForFilesAsync(
            files.Where(file => ownedLibraryIds.Contains(file.LibraryId)).Select(file => file.Id).ToArray(), cancellationToken);
        var ownerProfiles = access.IsOwner
            ? new Dictionary<Guid, ShareRecipientProfile>()
            : await recipientDirectory.FindByIdsAsync([access.OwnerUserId], cancellationToken);
        var facts = (await MemberFactsAsync([albumId], cancellationToken)).GetValueOrDefault(albumId);
        var albumRow = new AlbumRow(access.Album, access.OwnerUserId, access.IsOwner, access.SharedAt, access.Role);

        return new AlbumDetailsRes
        {
            Album = ToAlbumResponse(albumRow, facts, ownerProfiles.GetValueOrDefault(access.OwnerUserId)),
            Items = files.Select(file => MediaCatalogService.ToFileResponse(
                file,
                favorites.Contains(file.Id),
                readOnly: !ownedLibraryIds.Contains(file.LibraryId),
                sharedByDisplayName: ownerProfiles.GetValueOrDefault(access.OwnerUserId)?.DisplayName,
                sharedAt: access.SharedAt,
                tags: tags.GetValueOrDefault(file.Id))).ToList(),
            AvailableYears = availableYears,
            NextCursor = hasMore ? EncodeCursor(offset.Value + files.Count) : null
        };
    }

    public async Task<Result<AlbumRes>> CreateAsync(Guid userId, CreateAlbumReq request, CancellationToken cancellationToken)
    {
        var values = Validate(request.Name, request.Description);
        if (!values.IsSuccess) return values.ErrorValue;
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var now = DateTime.UtcNow;
        var album = new Album
        {
            LibraryId = library.Id,
            Name = values.SuccessValue.Name,
            Description = values.SuccessValue.Description,
            CreatedAt = now,
            ModifiedAt = now
        };
        dbContext.Albums.Add(album);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.albums] album created albumId={AlbumId} libraryId={LibraryId}", album.Id, album.LibraryId);
        return ToAlbumResponse(new AlbumRow(album, userId, true, null), null, null);
    }

    public async Task<Result<AlbumRes>> UpdateAsync(Guid userId, Guid albumId, UpdateAlbumReq request, CancellationToken cancellationToken)
    {
        var values = Validate(request.Name, request.Description);
        if (!values.IsSuccess) return values.ErrorValue;
        var owned = await OwnedAlbumAsync(userId, albumId, cancellationToken);
        if (owned is null) return MediaErrors.NotFound("The album was not found in your library.");
        owned.Name = values.SuccessValue.Name;
        owned.Description = values.SuccessValue.Description;
        owned.ModifiedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        var facts = (await MemberFactsAsync([albumId], cancellationToken)).GetValueOrDefault(albumId);
        logger.LogInformation("[media.albums] album updated albumId={AlbumId}", albumId);
        return ToAlbumResponse(new AlbumRow(owned, userId, true, null), facts, null);
    }

    public async Task<Result> DeleteAsync(Guid userId, Guid albumId, CancellationToken cancellationToken)
    {
        var owned = await OwnedAlbumAsync(userId, albumId, cancellationToken);
        if (owned is null) return MediaErrors.NotFound("The album was not found in your library.");
        dbContext.Albums.Remove(owned);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.albums] album deleted albumId={AlbumId}", albumId);
        return Result.Success();
    }

    public async Task<Result> AddItemAsync(Guid userId, Guid albumId, Guid fileId, CancellationToken cancellationToken)
    {
        var editable = await EditableAlbumAsync(userId, albumId, cancellationToken);
        if (editable is null) return MediaErrors.NotFound("The album was not found or cannot be edited.");
        var eligible = await dbContext.Files.AsNoTracking().AnyAsync(file =>
            file.Id == fileId && dbContext.Libraries.Any(library => library.Id == file.LibraryId && library.OwnerUserId == userId)
            && file.TrashedAt == null
            && (file.MimeType.StartsWith("image/") || file.MimeType.StartsWith("video/")), cancellationToken);
        if (!eligible) return MediaErrors.NotFound("Choose an active image or video from your library.");
        var exists = await dbContext.AlbumItems.AnyAsync(item => item.AlbumId == albumId && item.FileId == fileId, cancellationToken);
        if (!exists)
        {
            var now = DateTime.UtcNow;
            dbContext.AlbumItems.Add(new AlbumItem { AlbumId = albumId, FileId = fileId, AddedByUserId = userId, AddedAt = now });
            editable.ModifiedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[media.albums] album member added albumId={AlbumId} fileId={FileId}", albumId, fileId);
        }
        return Result.Success();
    }

    public async Task<Result> RemoveItemAsync(Guid userId, Guid albumId, Guid fileId, CancellationToken cancellationToken)
    {
        var editable = await EditableAlbumAsync(userId, albumId, cancellationToken);
        if (editable is null) return MediaErrors.NotFound("The album was not found or cannot be edited.");
        var membership = await dbContext.AlbumItems.SingleOrDefaultAsync(item => item.AlbumId == albumId && item.FileId == fileId, cancellationToken);
        if (membership is not null)
        {
            dbContext.AlbumItems.Remove(membership);
            editable.ModifiedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("[media.albums] album member removed albumId={AlbumId} fileId={FileId}", albumId, fileId);
        }
        return Result.Success();
    }

    public async Task<Result<BatchItemsRes>> SetItemsAsync(
        Guid userId,
        Guid albumId,
        SetAlbumItemsReq request,
        CancellationToken cancellationToken)
    {
        var itemIds = request.ItemIds ?? [];
        if (itemIds.Length is < 1 or > MaximumBatchItems)
            return MediaErrors.Validation($"Select between 1 and {MaximumBatchItems} items.");
        if (itemIds.Distinct().Count() != itemIds.Length)
            return MediaErrors.Validation("Album item IDs must be unique.");

        var editable = await EditableAlbumAsync(userId, albumId, cancellationToken);
        if (editable is null) return MediaErrors.NotFound("The album was not found or cannot be edited.");

        var ids = itemIds.ToHashSet();
        var files = await dbContext.Files
            .AsNoTracking()
            .Where(file => ids.Contains(file.Id)
                && (request.Included
                    ? dbContext.Libraries.Any(library => library.Id == file.LibraryId && library.OwnerUserId == userId)
                    : dbContext.AlbumItems.Any(member => member.AlbumId == albumId && member.FileId == file.Id)
                        || dbContext.Libraries.Any(library => library.Id == file.LibraryId && library.OwnerUserId == userId)))
            .ToDictionaryAsync(file => file.Id, cancellationToken);
        var memberships = await dbContext.AlbumItems
            .Where(item => item.AlbumId == albumId && ids.Contains(item.FileId))
            .ToDictionaryAsync(item => item.FileId, cancellationToken);
        var results = new List<BatchItemResultRes>(itemIds.Length);
        var now = DateTime.UtcNow;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var itemId in itemIds)
        {
            if (!files.TryGetValue(itemId, out var file))
            {
                results.Add(CatalogBatchService.Outcome(
                    itemId,
                    "failed",
                    MediaErrors.NotFoundCode,
                    "The requested item was not found in your library."));
                continue;
            }

            if (request.Included)
            {
                if (file.TrashedAt.HasValue
                    || !(file.MimeType.StartsWith("image/") || file.MimeType.StartsWith("video/")))
                {
                    results.Add(CatalogBatchService.Outcome(
                        itemId,
                        "failed",
                        MediaErrors.ConflictCode,
                        "Choose an active image or video from your library."));
                    continue;
                }
                if (memberships.ContainsKey(itemId))
                {
                    results.Add(CatalogBatchService.Outcome(itemId, "unchanged"));
                    continue;
                }
                dbContext.AlbumItems.Add(new AlbumItem
                {
                    AlbumId = albumId,
                    FileId = itemId,
                    AddedByUserId = userId,
                    AddedAt = now
                });
            }
            else
            {
                if (!memberships.TryGetValue(itemId, out var membership))
                {
                    results.Add(CatalogBatchService.Outcome(itemId, "unchanged"));
                    continue;
                }
                dbContext.AlbumItems.Remove(membership);
            }
            results.Add(CatalogBatchService.Outcome(itemId, "succeeded"));
        }

        if (results.Any(result => result.Status == "succeeded"))
            editable.ModifiedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var summary = CatalogBatchService.Summarize(itemIds.Length, results);
        logger.LogInformation(
            "[media.albums] album membership batch completed albumId={AlbumId} included={Included} requestedCount={RequestedCount} succeededCount={SucceededCount} unchangedCount={UnchangedCount} failedCount={FailedCount}",
            albumId,
            request.Included,
            summary.RequestedCount,
            summary.SucceededCount,
            summary.UnchangedCount,
            summary.FailedCount);
        return summary;
    }

    public async Task<Result<AlbumSharesRes>> ListSharesAsync(Guid userId, Guid albumId, CancellationToken cancellationToken)
    {
        if (await OwnedAlbumAsync(userId, albumId, cancellationToken) is null)
            return MediaErrors.NotFound("The album was not found in your library.");
        var shares = await dbContext.CatalogShares.AsNoTracking()
            .Where(share => share.AlbumId == albumId).OrderBy(share => share.CreatedAt).ToListAsync(cancellationToken);
        var profiles = await recipientDirectory.FindByIdsAsync(shares.Select(share => share.RecipientUserId).Distinct().ToArray(), cancellationToken);
        return new AlbumSharesRes
        {
            AlbumId = albumId,
            Shares = shares.Select(share => ToShareResponse(share, profiles.GetValueOrDefault(share.RecipientUserId))).ToList()
        };
    }

    public async Task<Result<ItemShareRes>> SetShareAsync(Guid userId, Guid albumId, Guid recipientUserId, SetItemShareReq request, CancellationToken cancellationToken)
    {
        var role = request.Role?.Trim().ToLowerInvariant();
        if (role is not (CatalogShare.ViewerRole or CatalogShare.CollaboratorRole))
            return MediaErrors.Validation("Album sharing role must be viewer or collaborator.");
        if (recipientUserId == userId) return MediaErrors.Validation("You cannot share an album with yourself.");
        var owned = await OwnedAlbumAsync(userId, albumId, cancellationToken);
        if (owned is null) return MediaErrors.NotFound("The album was not found in your library.");
        var recipient = await recipientDirectory.FindEligibleAsync(recipientUserId, cancellationToken);
        if (recipient is null) return MediaErrors.Validation("Choose an available family member.");
        var share = await dbContext.CatalogShares.SingleOrDefaultAsync(candidate =>
            candidate.AlbumId == albumId && candidate.RecipientUserId == recipientUserId, cancellationToken);
        var now = DateTime.UtcNow;
        if (share is null)
        {
            share = new CatalogShare
            {
                LibraryId = owned.LibraryId,
                AlbumId = albumId,
                RecipientUserId = recipientUserId,
                GrantedByUserId = userId,
                Role = role,
                CreatedAt = now,
                ModifiedAt = now
            };
            dbContext.CatalogShares.Add(share);
        }
        else
        {
            share.Role = role;
            share.GrantedByUserId = userId;
            share.ModifiedAt = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.albums] album grant saved albumId={AlbumId} recipientUserId={RecipientUserId}", albumId, recipientUserId);
        return ToShareResponse(share, recipient);
    }

    public async Task<Result> RevokeShareAsync(Guid userId, Guid albumId, Guid recipientUserId, CancellationToken cancellationToken)
    {
        if (await OwnedAlbumAsync(userId, albumId, cancellationToken) is null)
            return MediaErrors.NotFound("The album was not found in your library.");
        var share = await dbContext.CatalogShares.SingleOrDefaultAsync(candidate =>
            candidate.AlbumId == albumId && candidate.RecipientUserId == recipientUserId, cancellationToken);
        if (share is null) return MediaErrors.NotFound("The album share was not found.");
        dbContext.CatalogShares.Remove(share);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.albums] album grant revoked albumId={AlbumId} recipientUserId={RecipientUserId}", albumId, recipientUserId);
        return Result.Success();
    }

    private Task<Album?> OwnedAlbumAsync(Guid userId, Guid albumId, CancellationToken cancellationToken) =>
        dbContext.Albums.SingleOrDefaultAsync(album => album.Id == albumId
            && dbContext.Libraries.Any(library => library.Id == album.LibraryId && library.OwnerUserId == userId), cancellationToken);

    private Task<Album?> EditableAlbumAsync(Guid userId, Guid albumId, CancellationToken cancellationToken) =>
        dbContext.Albums.SingleOrDefaultAsync(album => album.Id == albumId
            && (dbContext.Libraries.Any(library => library.Id == album.LibraryId && library.OwnerUserId == userId)
                || dbContext.CatalogShares.Any(share => share.AlbumId == albumId
                    && share.RecipientUserId == userId && share.Role == CatalogShare.CollaboratorRole)), cancellationToken);

    private async Task<AlbumAccess?> AlbumAccessAsync(Guid userId, Guid albumId, CancellationToken cancellationToken)
    {
        var row = await (from album in dbContext.Albums.AsNoTracking()
                         join library in dbContext.Libraries.AsNoTracking() on album.LibraryId equals library.Id
                         where album.Id == albumId
                         select new { Album = album, library.OwnerUserId }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        if (row.OwnerUserId == userId) return new AlbumAccess(row.Album, row.OwnerUserId, true, null);
        var share = await dbContext.CatalogShares.AsNoTracking()
            .SingleOrDefaultAsync(share => share.AlbumId == albumId && share.RecipientUserId == userId
                && (share.Role == CatalogShare.ViewerRole || share.Role == CatalogShare.CollaboratorRole), cancellationToken);
        return share is not null ? new AlbumAccess(row.Album, row.OwnerUserId, false, share.CreatedAt, share.Role) : null;
    }

    private async Task<Dictionary<Guid, MemberFacts>> MemberFactsAsync(Guid[] albumIds, CancellationToken cancellationToken)
    {
        if (albumIds.Length == 0) return [];
        var rows = await (from membership in dbContext.AlbumItems.AsNoTracking()
                          join file in dbContext.Files.AsNoTracking() on membership.FileId equals file.Id
                          where albumIds.Contains(membership.AlbumId) && file.TrashedAt == null
                          orderby membership.AddedAt descending, membership.FileId
                          select new { membership.AlbumId, membership.FileId }).ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.AlbumId).ToDictionary(
            group => group.Key,
            group =>
            {
                var members = group.ToList();
                var covers = members
                    .Take(members.Count >= 3 ? 4 : 1)
                    .Select(member => member.FileId)
                    .ToArray();
                return new MemberFacts(members.Count, covers);
            });
    }

    private async Task<Dictionary<Guid, IReadOnlyList<TagSummaryRes>>> TagsForFilesAsync(
        Guid[] fileIds,
        CancellationToken cancellationToken)
    {
        if (fileIds.Length == 0) return [];
        var rows = await (
            from assignment in dbContext.FileTags.AsNoTracking()
            join tag in dbContext.Tags.AsNoTracking() on assignment.TagId equals tag.Id
            where fileIds.Contains(assignment.FileId)
            orderby tag.Name
            select new TagItemRow(assignment.FileId, tag.Id, tag.Name, tag.Color))
            .ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.FileId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TagSummaryRes>)group.Select(row => new TagSummaryRes
                {
                    Id = row.TagId,
                    Name = row.Name,
                    Color = row.Color
                }).ToList());
    }

    private static AlbumRes ToAlbumResponse(AlbumRow row, MemberFacts? facts, ShareRecipientProfile? owner) => new()
    {
        Id = row.Album.Id,
        Name = row.Album.Name,
        Description = row.Album.Description,
        MemberCount = facts?.Count ?? 0,
        CoverFileId = facts is { CoverFileIds.Count: > 0 }
            ? facts.CoverFileIds[0]
            : null,
        CoverFileIds = facts?.CoverFileIds ?? [],
        CreatedAt = row.Album.CreatedAt,
        ModifiedAt = row.Album.ModifiedAt,
        AccessRole = row.IsOwner ? "owner" : row.Role,
        SharedByDisplayName = row.IsOwner ? null : owner?.DisplayName ?? "Family member",
        SharedAt = row.SharedAt
    };

    private static ItemShareRes ToShareResponse(CatalogShare share, ShareRecipientProfile? profile) => new()
    {
        RecipientUserId = share.RecipientUserId,
        RecipientDisplayName = profile?.DisplayName ?? "Family member",
        RecipientEmail = profile?.Email ?? string.Empty,
        Role = share.Role,
        SharedAt = share.CreatedAt
    };

    private static Result<AlbumValues> Validate(string? name, string? description)
    {
        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName)) return MediaErrors.Validation("Album name is required.");
        if (normalizedName.Length > 200) return MediaErrors.Validation("Album name must be 200 characters or fewer.");
        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (normalizedDescription?.Length > 2000) return MediaErrors.Validation("Album description must be 2000 characters or fewer.");
        return new AlbumValues(normalizedName, normalizedDescription);
    }

    private static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"album:{offset}"));

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static int? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return value.StartsWith("album:", StringComparison.Ordinal)
                && int.TryParse(value[6..], out var offset) && offset >= 0 ? offset : null;
        }
        catch (FormatException) { return null; }
    }

    private sealed record AlbumValues(string Name, string? Description);
    private sealed record AlbumRow(Album Album, Guid OwnerUserId, bool IsOwner, DateTime? SharedAt, string Role = CatalogShare.ViewerRole);
    private sealed record AlbumAccess(Album Album, Guid OwnerUserId, bool IsOwner, DateTime? SharedAt, string Role = CatalogShare.ViewerRole);
    private sealed record MemberFacts(int Count, IReadOnlyList<Guid> CoverFileIds);
    private sealed record TagItemRow(Guid FileId, Guid TagId, string Name, string Color);
}
