using System.Globalization;
using System.Text;
using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class TagService(
    MediaCatalogDbContext dbContext,
    LibraryBootstrapper libraryBootstrapper) : ITagService
{
    private const int MaximumItems = 300;
    private const int MaximumTagsPerLibrary = 500;
    private const int MaximumTagsPerItem = 50;
    private static readonly HashSet<string> Colors =
        ["gray", "red", "orange", "yellow", "green", "blue", "purple", "pink"];

    public async Task<Result<IReadOnlyList<TagRes>>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        return await dbContext.Tags.AsNoTracking()
            .Where(tag => tag.LibraryId == library.Id)
            .OrderBy(tag => tag.Name)
            .Select(tag => new TagRes
            {
                Id = tag.Id,
                Name = tag.Name,
                Color = tag.Color,
                FileCount = dbContext.FileTags.Count(item => item.TagId == tag.Id),
                FolderCount = dbContext.FolderTags.Count(item => item.TagId == tag.Id)
            }).ToListAsync(cancellationToken);
    }

    public async Task<Result<TagRes>> CreateAsync(Guid userId, CreateTagReq request, CancellationToken cancellationToken)
    {
        var normalized = Normalize(request.Name, request.Color);
        if (normalized.IsFailure) return normalized.ErrorValue;
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        if (await dbContext.Tags.CountAsync(tag => tag.LibraryId == library.Id, cancellationToken) >= MaximumTagsPerLibrary)
            return MediaErrors.Conflict($"A library may contain at most {MaximumTagsPerLibrary} tags.");
        if (await dbContext.Tags.AnyAsync(tag => tag.LibraryId == library.Id && tag.NormalizedName == normalized.SuccessValue.NormalizedName, cancellationToken))
            return MediaErrors.Conflict("A tag with this name already exists.");
        var tag = new Tag
        {
            LibraryId = library.Id,
            Name = normalized.SuccessValue.Name,
            NormalizedName = normalized.SuccessValue.NormalizedName,
            Color = normalized.SuccessValue.Color,
            CreatedByUserId = userId
        };
        dbContext.Tags.Add(tag);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(tag);
    }

    public async Task<Result<TagRes>> UpdateAsync(Guid userId, Guid tagId, UpdateTagReq request, CancellationToken cancellationToken)
    {
        var normalized = Normalize(request.Name, request.Color);
        if (normalized.IsFailure) return normalized.ErrorValue;
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var tag = await dbContext.Tags.SingleOrDefaultAsync(item => item.Id == tagId && item.LibraryId == library.Id, cancellationToken);
        if (tag is null) return MediaErrors.NotFound("The requested tag was not found.");
        if (await dbContext.Tags.AnyAsync(item => item.LibraryId == library.Id && item.Id != tag.Id && item.NormalizedName == normalized.SuccessValue.NormalizedName, cancellationToken))
            return MediaErrors.Conflict("A tag with this name already exists.");
        tag.Name = normalized.SuccessValue.Name;
        tag.NormalizedName = normalized.SuccessValue.NormalizedName;
        tag.Color = normalized.SuccessValue.Color;
        tag.ModifiedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(tag,
            await dbContext.FileTags.CountAsync(item => item.TagId == tag.Id, cancellationToken),
            await dbContext.FolderTags.CountAsync(item => item.TagId == tag.Id, cancellationToken));
    }

    public async Task<Result> DeleteAsync(Guid userId, Guid tagId, CancellationToken cancellationToken)
    {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var tag = await dbContext.Tags.SingleOrDefaultAsync(item => item.Id == tagId && item.LibraryId == library.Id, cancellationToken);
        if (tag is null) return MediaErrors.NotFound("The requested tag was not found.");
        dbContext.Tags.Remove(tag);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<BatchItemsRes>> SetItemsAsync(Guid userId, Guid tagId, SetTagItemsReq request, CancellationToken cancellationToken)
    {
        var itemIds = request.ItemIds ?? [];
        if (itemIds.Length is < 1 or > MaximumItems)
            return MediaErrors.Validation($"Select between 1 and {MaximumItems} items.");
        if (itemIds.Distinct().Count() != itemIds.Length)
            return MediaErrors.Validation("Tag item IDs must be unique.");
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var tag = await dbContext.Tags.SingleOrDefaultAsync(item => item.Id == tagId && item.LibraryId == library.Id, cancellationToken);
        if (tag is null) return MediaErrors.NotFound("The requested tag was not found.");

        var ids = itemIds.ToHashSet();
        var files = await dbContext.Files.Where(file => ids.Contains(file.Id) && file.LibraryId == library.Id).ToDictionaryAsync(file => file.Id, cancellationToken);
        var folders = await dbContext.Folders.Where(folder => ids.Contains(folder.Id) && folder.LibraryId == library.Id).ToDictionaryAsync(folder => folder.Id, cancellationToken);
        var existingFiles = await dbContext.FileTags.Where(item => item.TagId == tag.Id && ids.Contains(item.FileId)).ToDictionaryAsync(item => item.FileId, cancellationToken);
        var existingFolders = await dbContext.FolderTags.Where(item => item.TagId == tag.Id && ids.Contains(item.FolderId)).ToDictionaryAsync(item => item.FolderId, cancellationToken);
        var results = new List<BatchItemResultRes>();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var itemId in itemIds)
        {
            if (files.TryGetValue(itemId, out var file))
            {
                if (file.TrashedAt.HasValue)
                {
                    results.Add(CatalogBatchService.Outcome(itemId, "failed", MediaErrors.ConflictCode, "Restore this item before changing its tags."));
                    continue;
                }
                if (request.Included == existingFiles.ContainsKey(itemId))
                {
                    results.Add(CatalogBatchService.Outcome(itemId, "unchanged"));
                    continue;
                }
                if (request.Included)
                {
                    if (await dbContext.FileTags.CountAsync(item => item.FileId == itemId, cancellationToken) >= MaximumTagsPerItem)
                    {
                        results.Add(CatalogBatchService.Outcome(itemId, "failed", MediaErrors.ConflictCode, $"An item may contain at most {MaximumTagsPerItem} tags."));
                        continue;
                    }
                    dbContext.FileTags.Add(new FileTag { FileId = itemId, TagId = tag.Id, AddedByUserId = userId });
                }
                else dbContext.FileTags.Remove(existingFiles[itemId]);
                results.Add(CatalogBatchService.Outcome(itemId, "succeeded"));
                continue;
            }
            if (folders.TryGetValue(itemId, out var folder))
            {
                if (folder.TrashedAt.HasValue || folder.SystemRole is not null)
                {
                    results.Add(CatalogBatchService.Outcome(itemId, "failed", MediaErrors.ConflictCode, "This folder cannot have its tags changed."));
                    continue;
                }
                if (request.Included == existingFolders.ContainsKey(itemId))
                {
                    results.Add(CatalogBatchService.Outcome(itemId, "unchanged"));
                    continue;
                }
                if (request.Included)
                {
                    if (await dbContext.FolderTags.CountAsync(item => item.FolderId == itemId, cancellationToken) >= MaximumTagsPerItem)
                    {
                        results.Add(CatalogBatchService.Outcome(itemId, "failed", MediaErrors.ConflictCode, $"An item may contain at most {MaximumTagsPerItem} tags."));
                        continue;
                    }
                    dbContext.FolderTags.Add(new FolderTag { FolderId = itemId, TagId = tag.Id, AddedByUserId = userId });
                }
                else dbContext.FolderTags.Remove(existingFolders[itemId]);
                results.Add(CatalogBatchService.Outcome(itemId, "succeeded"));
                continue;
            }
            results.Add(CatalogBatchService.Outcome(itemId, "failed", MediaErrors.NotFoundCode, "The requested item was not found in your library."));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CatalogBatchService.Summarize(itemIds.Length, results);
    }

    private static Result<(string Name, string NormalizedName, string Color)> Normalize(string? name, string? color)
    {
        var display = name?.Trim().Normalize(NormalizationForm.FormKC);
        if (string.IsNullOrWhiteSpace(display) || display.Length > 64 || display.Any(char.IsControl))
            return MediaErrors.Validation("Tag names must contain between 1 and 64 visible characters.");
        var selectedColor = color?.Trim().ToLowerInvariant() ?? "gray";
        if (!Colors.Contains(selectedColor))
            return MediaErrors.Validation("Tag color must be gray, red, orange, yellow, green, blue, purple, or pink.");
        return (display, display.ToUpper(CultureInfo.InvariantCulture), selectedColor);
    }

    private static TagRes ToResponse(Tag tag, int files = 0, int folders = 0) => new()
    {
        Id = tag.Id,
        Name = tag.Name,
        Color = tag.Color,
        FileCount = files,
        FolderCount = folders
    };
}
