using System.Security.Claims;
using Asp.Versioning;
using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Utils.ApiHelpers.Filters;
using MediaExplorer.Media.Api.Http;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MediaExplorer.Media.Api.Controllers.v1;

/// <summary>Previews and runs the single restart-safe installation-wide owner cache operation.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/system/cache-builds")]
[Authorize(Policy = "Owner")]
public sealed class CacheBuildsController(ICacheBuildService cacheBuilds) : ControllerBase
{
    /// <summary>Returns cache capacity, disk headroom, catalog totals, and cache coverage.</summary>
    [HttpGet("options")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<CacheBuildOptionsRes>))]
    public async Task<IActionResult> Options(CancellationToken cancellationToken) =>
        this.ToApiResult(await cacheBuilds.GetOptionsAsync(cancellationToken));

    /// <summary>Calculates missing thumbnails, originals, and download bytes without starting work.</summary>
    [HttpPost("preview")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<CacheBuildPreviewRes>))]
    public async Task<IActionResult> Preview([FromBody] PreviewCacheBuildReq request, CancellationToken cancellationToken) =>
        this.ToApiResult(await cacheBuilds.PreviewAsync(request, cancellationToken));

    /// <summary>Starts a durable missing-only cache build after rechecking the reviewed preview totals.</summary>
    [HttpPost]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ApiRes<CacheBuildJobRes>))]
    public async Task<IActionResult> Start([FromBody] StartCacheBuildReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await cacheBuilds.StartAsync(userId, request, cancellationToken), StatusCodes.Status201Created);
    }

    /// <summary>Returns the current or last installation-wide cache operation.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<IReadOnlyList<CacheBuildJobRes>>))]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        this.ToApiResult(await cacheBuilds.ListAsync(cancellationToken));

    /// <summary>Returns current aggregate status for one cache-build job.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<CacheBuildJobRes>))]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        this.ToApiResult(await cacheBuilds.GetAsync(id, cancellationToken));

    /// <summary>Requests cooperative cancellation between cache items.</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) =>
        this.ToApiResult(await cacheBuilds.CancelAsync(id, cancellationToken));

    /// <summary>Requeues failed cache items without repeating completed work.</summary>
    [HttpPost("{id:guid}/retry")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken) =>
        this.ToApiResult(await cacheBuilds.RetryFailedAsync(id, cancellationToken));

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
