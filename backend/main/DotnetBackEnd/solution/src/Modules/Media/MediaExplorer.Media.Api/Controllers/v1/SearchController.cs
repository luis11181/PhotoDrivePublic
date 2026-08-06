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

/// <summary>Searches the authenticated member's indexed SQLite catalog.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/search")]
[Authorize]
public sealed class SearchController(ICatalogQueryService queries, ISharingService sharing) : ControllerBase
{
    /// <summary>Returns cursor-paginated catalog name matches without scanning S3 or local caches.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<BrowseItemsRes>))]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] string scope,
        [FromQuery] string photoScope = "folder",
        [FromQuery] string type = "all",
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 60,
        [FromQuery] Guid? tagId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = string.Equals(scope.Trim(), "shared", StringComparison.OrdinalIgnoreCase)
            ? await sharing.SearchSharedAsync(userId, q, type, cursor, Math.Clamp(limit, 1, 100), tagId, cancellationToken)
            : await queries.SearchAsync(userId, q, scope, photoScope, type, cursor, Math.Clamp(limit, 1, 100), tagId, cancellationToken);
        return this.ToApiResult(result);
    }
}
