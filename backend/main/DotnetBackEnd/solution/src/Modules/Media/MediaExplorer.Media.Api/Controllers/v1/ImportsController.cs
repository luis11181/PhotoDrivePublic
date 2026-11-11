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

/// <summary>Starts and monitors authenticated imports from server-local sources.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/imports")]
[Authorize(Policy = "Owner")]
public sealed class ImportsController(IImportService imports) : ControllerBase
{
    /// <summary>Browses relative subfolders of the configured read-only server import inbox. Owner only.</summary>
    [HttpGet("source-folders")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ImportSourceFoldersRes>))]
    public IActionResult SourceFolders([FromQuery] string? path, [FromQuery] string? cursor, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
        => this.ToApiResult(imports.ListSourceFolders(path, cursor, Math.Clamp(limit, 1, 100), cancellationToken));
    /// <summary>Returns the caller's active catalog folders that may receive an import.</summary>
    [HttpGet("options")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ImportOptionsRes>))]
    public async Task<IActionResult> Options(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await imports.GetOptionsAsync(userId, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Lists the caller's latest durable import jobs without exposing absolute source paths.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<IReadOnlyList<ImportJobRes>>))]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await imports.ListAsync(userId, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Returns aggregate status for one owned import job.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ImportJobRes>))]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await imports.GetAsync(userId, id, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Lists paginated source-relative outcomes and warnings for one import job.</summary>
    [HttpGet("{id:guid}/items")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ImportItemResultsRes>))]
    public async Task<IActionResult> Items(
        Guid id,
        [FromQuery] string category = "all",
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await imports.ListItemsAsync(
            userId,
            id,
            category,
            cursor,
            Math.Clamp(limit, 1, 100),
            cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Scans an extracted Google Photos Takeout directory and queues every discovered media item.</summary>
    /// <remarks>The source is relative to the configured import inbox. Absolute paths, traversal and symbolic links are rejected. Processing continues in the restart-safe background queue after this request returns.</remarks>
    [HttpPost("google-takeout")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ApiRes<ImportJobRes>))]
    public async Task<IActionResult> Start(
        [FromBody] CreateGoogleTakeoutImportReq request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await imports.StartGoogleTakeoutAsync(userId, request, cancellationToken);
        return this.ToApiResult(result, StatusCodes.Status201Created);
    }

    /// <summary>Requeues only failed items from an owned import job.</summary>
    [HttpPost("{id:guid}/retry")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ImportJobRes>))]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await imports.RetryFailedAsync(userId, id, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Removes a finished owned import job and its per-file results.</summary>
    /// <remarks>Files the import already added stay in the library. Running jobs return 409. Removing a job with failed items gives up retrying them.</remarks>
    [HttpDelete("{id:guid}")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes))]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return (await imports.DeleteAsync(userId, id, cancellationToken)).Match<IActionResult>(
            () => Ok(ApiRes.Success()), error => this.ToApiError(error));
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
