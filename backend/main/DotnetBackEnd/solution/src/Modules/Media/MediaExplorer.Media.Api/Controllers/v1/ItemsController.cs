using System.Security.Claims;
using Asp.Versioning;
using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Shared.Utils.ApiHelpers.Filters;
using MediaExplorer.Media.Api.Http;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MediaExplorer.Media.Api.Controllers.v1;

/// <summary>Browses and manages the authenticated member's catalog items and media delivery.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/items")]
[Authorize]
public sealed class ItemsController(
    ICatalogQueryService queries,
    ICatalogCommandService commands,
    ICatalogBatchService batches,
    IMediaDeliveryService delivery) : ControllerBase
{
    /// <summary>Queries active owned Files by immediate children, recursive subtree, or the complete Documents tree.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<BrowseItemsRes>))]
    public async Task<IActionResult> Query(
        [FromQuery] string? q = null,
        [FromQuery] string location = "children",
        [FromQuery] Guid? folderId = null,
        [FromQuery] bool includePhotos = false,
        [FromQuery] bool favorite = false,
        [FromQuery] string type = "all",
        [FromQuery] string sort = "name_asc",
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 60,
        [FromQuery] Guid? tagId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await queries.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = q,
            Location = location,
            FolderId = folderId,
            IncludePhotos = includePhotos,
            Favorite = favorite,
            Type = type,
            Sort = sort,
            Cursor = cursor,
            Limit = Math.Clamp(limit, 1, 100),
            TagId = tagId
        }, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Lists the authenticated member's active favorite folders for Quick Access, including authorized shared folders.</summary>
    [HttpGet("favorite-folders")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<BrowseItemsRes>))]
    public async Task<IActionResult> FavoriteFolders(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 60,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await queries.BrowseFavoriteFoldersAsync(
            userId,
            cursor,
            Math.Clamp(limit, 1, 100),
            cancellationToken));
    }

    /// <summary>Lists soft-trashed catalog roots for the Trash view.</summary>
    [HttpGet("trash")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<BrowseItemsRes>))]
    public async Task<IActionResult> ListTrash(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 60,
        [FromQuery] string type = "all",
        [FromQuery] string sort = "name_asc",
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await queries.BrowseTrashAsync(
            userId,
            cursor,
            Math.Clamp(limit, 1, 100),
            type,
            sort,
            cancellationToken));
    }

    /// <summary>Returns the exact current number of files and folders that Clear Trash can permanently remove.</summary>
    [HttpGet("trash/summary")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<TrashSummaryRes>))]
    public async Task<IActionResult> TrashSummary(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await queries.GetTrashSummaryAsync(userId, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Previews the deduplicated Files, Photos, and album impact of moving up to 300 selected roots to Trash.</summary>
    [HttpPost("trash/preview")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<TrashPreviewRes>))]
    public async Task<IActionResult> TrashPreview([FromBody] TrashPreviewReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await queries.PreviewTrashAsync(userId, request.ItemIds ?? [], cancellationToken));
    }

    /// <summary>Returns cached SQLite-derived storage usage; set refresh=true to recalculate it without contacting S3.</summary>
    [HttpGet("storage-summary")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<StorageSummaryRes>))]
    public async Task<IActionResult> StorageSummary([FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await queries.GetStorageSummaryAsync(userId, refresh, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Gets catalog metadata and current per-user state for one item.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await queries.GetItemAsync(userId, id, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Renames a live file or folder in SQLite only.</summary>
    [HttpPatch("{id:guid}")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameItemReq request, CancellationToken cancellationToken) =>
        await ItemCommand((userId, token) => commands.RenameItemAsync(userId, id, request, token), cancellationToken);

    /// <summary>Moves a live item in SQLite and rejects folder cycles.</summary>
    [HttpPost("{id:guid}/move")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveItemReq request, CancellationToken cancellationToken) =>
        await ItemCommand((userId, token) => commands.MoveItemAsync(userId, id, request, token), cancellationToken);

    /// <summary>Creates a new catalog file/version that reuses the same immutable original bytes.</summary>
    [HttpPost("{id:guid}/copy")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ApiRes<MediaItemRes>))]
    public async Task<IActionResult> Copy(Guid id, [FromBody] CopyFileReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await commands.CopyFileAsync(userId, id, request, cancellationToken);
        return this.ToApiResult(result, StatusCodes.Status201Created);
    }

    /// <summary>Sets the member's favorite state for a live file or folder.</summary>
    [HttpPost("{id:guid}/favorite")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Favorite(Guid id, [FromBody] SetFavoriteReq request, CancellationToken cancellationToken) =>
        await ItemCommand((userId, token) => commands.SetFavoriteAsync(userId, id, request, token), cancellationToken);

    /// <summary>Queues retry-safe extraction for the current immutable version.</summary>
    [HttpPost("{id:guid}/metadata/retry")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> RetryMetadata(Guid id, CancellationToken cancellationToken) =>
        await ItemCommand((userId, token) => commands.RetryMetadataAsync(userId, id, token), cancellationToken);

    /// <summary>Applies one user-corrected calendar capture date, or restores the detected date, for up to 300 active owned files.</summary>
    [HttpPost("metadata/capture-date/batch")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<BatchItemsRes>))]
    public async Task<IActionResult> SetCaptureDate([FromBody] SetCaptureDateReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await batches.SetCaptureDateAsync(userId, request, cancellationToken));
    }

    /// <summary>Moves an item or folder subtree to recoverable trash until it is manually cleared.</summary>
    [HttpPost("{id:guid}/trash")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Trash(Guid id, CancellationToken cancellationToken) =>
        await ItemCommand((userId, token) => commands.TrashItemAsync(userId, id, token), cancellationToken);

    /// <summary>Restores a trashed item or subtree until the user manually clears trash.</summary>
    [HttpPost("{id:guid}/restore")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken) =>
        await ItemCommand((userId, token) => commands.RestoreItemAsync(userId, id, token), cancellationToken);

    /// <summary>Applies one favorite, move, trash, or restore action to at most 300 unique item IDs with per-item outcomes.</summary>
    [HttpPost("batch")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<BatchItemsRes>))]
    public async Task<IActionResult> Batch([FromBody] BatchItemsReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await batches.ExecuteAsync(userId, request, cancellationToken));
    }

    /// <summary>Permanently removes every currently trashed catalog item and each unreferenced original's S3 versions after exact-count and typed confirmation.</summary>
    [HttpPost("trash/clear")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ClearTrashRes>))]
    public async Task<IActionResult> ClearTrash([FromBody] ClearTrashReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await commands.ClearTrashAsync(userId, request, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Creates a SQLite catalog folder; no S3 prefix is created.</summary>
    [HttpPost("folders")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ApiRes<MediaItemRes>))]
    public async Task<IActionResult> CreateFolder([FromBody] CreateFolderReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await commands.CreateFolderAsync(userId, request, cancellationToken);
        return this.ToApiResult(result, StatusCodes.Status201Created);
    }

    /// <summary>Downloads one original directly or streams 1–100 selected files and folder subtrees as a ZIP archive.</summary>
    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] Guid[] ids, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return await WriteDownloadAsync(
            await delivery.PrepareDownloadAsync(userId, ids, cancellationToken),
            cancellationToken);
    }

    /// <summary>Returns the deduplicated file count and source-byte total for a proposed item download.</summary>
    [HttpGet("download/preview")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<DownloadPreviewRes>))]
    public async Task<IActionResult> DownloadPreview([FromQuery] Guid[] ids, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await delivery.PreviewDownloadAsync(userId, ids, cancellationToken));
    }

    /// <summary>Streams one active folder subtree as a ZIP without retaining a server-side archive.</summary>
    [HttpGet("folders/{folderId:guid}/download")]
    public async Task<IActionResult> DownloadFolder(Guid folderId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return await WriteDownloadAsync(
            await delivery.PrepareFolderDownloadAsync(userId, folderId, cancellationToken),
            cancellationToken);
    }

    /// <summary>Returns the active descendant file count and source-byte total for a folder ZIP.</summary>
    [HttpGet("folders/{folderId:guid}/download/preview")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<DownloadPreviewRes>))]
    public async Task<IActionResult> DownloadFolderPreview(Guid folderId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await delivery.PreviewFolderDownloadAsync(userId, folderId, cancellationToken));
    }

    /// <summary>Returns authenticated original bytes with range and conditional-request support.</summary>
    [HttpGet("{id:guid}/content")]
    [HttpHead("{id:guid}/content")]
    public async Task<IActionResult> Content(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        if (!TryParseRange(Request.Headers.Range, out var start, out var end))
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable, ApiRes.RangeNotSatisfiable());
        if (HttpMethods.IsHead(Request.Method))
        {
            var infoResult = await delivery.GetContentInfoAsync(userId, id, cancellationToken);
            return infoResult.Match<IActionResult>(
                onSuccess: info => WriteBinaryInfo(info, start, end),
                onFailure: error => this.ToApiError(error));
        }
        var result = await delivery.OpenContentAsync(userId, id, start, end, cancellationToken);
        return await result.Match<Task<IActionResult>>(
            onSuccess: async content =>
            {
                using (content) return await WriteBinaryAsync(content, start, cancellationToken);
            },
            onFailure: error => Task.FromResult(this.ToApiError(error)));
    }

    /// <summary>Returns an authenticated rebuildable WebP thumbnail.</summary>
    [HttpGet("{id:guid}/thumbnail")]
    public async Task<IActionResult> Thumbnail(Guid id, [FromQuery] string size = "md", CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await delivery.OpenThumbnailAsync(userId, id, size, cancellationToken);
        return await result.Match<Task<IActionResult>>(
            onSuccess: async thumbnail =>
            {
                using (thumbnail) return await WriteBinaryAsync(thumbnail, null, cancellationToken);
            },
            onFailure: error => Task.FromResult(this.ToApiError(error)));
    }

    private async Task<IActionResult> ItemCommand(
        Func<Guid, CancellationToken, Task<Result<MediaItemRes>>> execute,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await execute(userId, cancellationToken);
        return this.ToApiResult(result);
    }

    private async Task<IActionResult> WriteBinaryAsync(MediaBinaryContent content, long? rangeStart, CancellationToken cancellationToken)
    {
        var etag = $"\"{content.Etag}\"";
        if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
            return StatusCode(StatusCodes.Status304NotModified);

        Response.Headers.ETag = etag;
        Response.Headers.LastModified = content.LastModified.ToString("R");
        Response.Headers.AcceptRanges = "bytes";
        Response.Headers.CacheControl = "private, max-age=86400";
        Response.ContentType = content.ContentType;
        Response.ContentLength = content.ContentLength;
        if (rangeStart.HasValue)
        {
            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.Headers.ContentRange = $"bytes {rangeStart.Value}-{rangeStart.Value + content.ContentLength - 1}/{content.TotalLength}";
        }
        if (!HttpMethods.IsHead(Request.Method)) await content.Content.CopyToAsync(Response.Body, cancellationToken);
        return new EmptyResult();
    }

    private IActionResult WriteBinaryInfo(MediaBinaryInfo info, long? rangeStart, long? rangeEnd)
    {
        if (rangeStart.HasValue && rangeStart.Value >= info.ContentLength)
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable, ApiRes.RangeNotSatisfiable());

        var etag = $"\"{info.Etag}\"";
        if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
            return StatusCode(StatusCodes.Status304NotModified);

        Response.Headers.ETag = etag;
        Response.Headers.LastModified = info.LastModified.ToString("R");
        Response.Headers.AcceptRanges = "bytes";
        Response.Headers.CacheControl = "private, max-age=86400";
        Response.ContentType = info.ContentType;
        if (rangeStart.HasValue)
        {
            var boundedEnd = Math.Min(rangeEnd ?? (info.ContentLength - 1), info.ContentLength - 1);
            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.ContentLength = boundedEnd - rangeStart.Value + 1;
            Response.Headers.ContentRange = $"bytes {rangeStart.Value}-{boundedEnd}/{info.ContentLength}";
        }
        else
        {
            Response.ContentLength = info.ContentLength;
        }
        return new EmptyResult();
    }

    private async Task<IActionResult> WriteDownloadAsync(
        Result<MediaDownloadContent> result,
        CancellationToken cancellationToken) =>
        await result.Match<Task<IActionResult>>(
            onSuccess: async download =>
            {
                Response.ContentType = download.ContentType;
                Response.Headers.CacheControl = "private, no-store";
                Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(download.FileName)}";
                if (download.ContentLength.HasValue) Response.ContentLength = download.ContentLength.Value;
                await download.WriteToAsync(Response.Body, cancellationToken);
                return new EmptyResult();
            },
            onFailure: error => Task.FromResult(this.ToApiError(error)));

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static bool TryParseRange(string? header, out long? start, out long? end)
    {
        start = end = null;
        if (string.IsNullOrWhiteSpace(header)) return true;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',')) return false;
        var parts = header[6..].Split('-', StringSplitOptions.None);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var parsedStart) || parsedStart < 0) return false;
        if (!string.IsNullOrEmpty(parts[1]) && (!long.TryParse(parts[1], out var parsedEnd) || parsedEnd < parsedStart)) return false;
        start = parsedStart;
        end = string.IsNullOrEmpty(parts[1]) ? null : long.Parse(parts[1]);
        return true;
    }
}
