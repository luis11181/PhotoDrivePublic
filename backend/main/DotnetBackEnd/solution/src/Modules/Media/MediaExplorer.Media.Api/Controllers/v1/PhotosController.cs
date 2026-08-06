using System.Security.Claims;
using Asp.Versioning;
using CSSApps.Shared.Contracts.APICommons;
using MediaExplorer.Media.Api.Http;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MediaExplorer.Media.Api.Controllers.v1;

/// <summary>Browses the authenticated member's chronological image/video timeline.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/photos")]
[Authorize]
public sealed class PhotosController(ICatalogQueryService queries) : ControllerBase
{
    /// <summary>Returns cursor-paginated media ordered by effective capture date.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<BrowseItemsRes>))]
    public async Task<IActionResult> Browse(
        [FromQuery] string scope = "folder",
        [FromQuery] Guid? folderId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 100,
        [FromQuery] string type = "all",
        [FromQuery] bool favorite = false,
        [FromQuery] string sort = "captured_desc",
        [FromQuery] int? year = null,
        [FromQuery] string? captureDate = null,
        [FromQuery] string? seekDate = null,
        [FromQuery] Guid? tagId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await queries.BrowsePhotosAsync(userId, scope, folderId, cursor, Math.Clamp(limit, 1, 100), type, favorite, sort, year, captureDate, seekDate, tagId, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Lists available timeline years for the selected SQLite-backed Photos filter.</summary>
    [HttpGet("years")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<PhotoTimelineYearsRes>))]
    public async Task<IActionResult> Years(
        [FromQuery] string scope = "folder",
        [FromQuery] Guid? folderId = null,
        [FromQuery] string type = "all",
        [FromQuery] bool favorite = false,
        [FromQuery] Guid? tagId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await queries.BrowsePhotoYearsAsync(userId, scope, folderId, type, favorite, tagId, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Returns filtered month counts and opaque cursors for direct timeline seeks.</summary>
    [HttpGet("timeline-anchors")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<PhotoTimelineAnchorsRes>))]
    public async Task<IActionResult> TimelineAnchors(
        [FromQuery] string scope = "folder",
        [FromQuery] Guid? folderId = null,
        [FromQuery] string type = "all",
        [FromQuery] bool favorite = false,
        [FromQuery] string sort = "captured_desc",
        [FromQuery] int? year = null,
        [FromQuery] string? captureDate = null,
        [FromQuery] Guid? tagId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await queries.BrowsePhotoTimelineAnchorsAsync(userId, scope, folderId, type, favorite, sort, year, captureDate, tagId, cancellationToken);
        return this.ToApiResult(result);
    }
}
