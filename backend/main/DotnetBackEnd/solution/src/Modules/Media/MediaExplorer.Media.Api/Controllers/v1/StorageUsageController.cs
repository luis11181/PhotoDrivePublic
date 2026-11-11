using System.Security.Claims;
using Asp.Versioning;
using CSSApps.Shared.Contracts.APICommons;
using MediaExplorer.Media.Api.Http;
using MediaExplorer.Media.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace MediaExplorer.Media.Api.Controllers.v1;
/// <summary>Catalog-derived original storage usage in the caller's library.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/storage-usage"), Authorize]
public sealed class StorageUsageController(IStorageUsageService service) : ControllerBase
{
    /// <summary>Cached original storage usage and breakdowns. Set refresh=true to recalculate from SQLite.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] bool refresh = false, CancellationToken cancellationToken = default) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? this.ToApiResult(await service.GetAsync(userId, refresh, cancellationToken)) : Unauthorized(ApiRes.Unauthorized());
    /// <summary>Lists up to 50 file-version references behind a breakdown, largest first.</summary>
    [HttpGet("files")]
    public async Task<IActionResult> Files([FromQuery] string category = "all", [FromQuery] string? month = null,
        [FromQuery] Guid? folderId = null, [FromQuery] int offset = 0, CancellationToken cancellationToken = default) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? this.ToApiResult(await service.FilesAsync(userId, category, month, folderId, offset, cancellationToken)) : Unauthorized(ApiRes.Unauthorized());
}
