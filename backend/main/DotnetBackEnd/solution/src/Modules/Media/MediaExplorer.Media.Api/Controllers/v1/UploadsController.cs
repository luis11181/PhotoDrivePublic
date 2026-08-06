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

/// <summary>Creates, resumes, and completes authenticated staged uploads.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/uploads")]
[Authorize]
public sealed class UploadsController(IUploadService uploads) : ControllerBase
{
    /// <summary>Checks selected names against active files in the resolved upload folder without exposing item identities.</summary>
    [HttpPost("name-conflicts")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> CheckNameConflicts([FromBody] CheckUploadNameConflictsReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await uploads.CheckNameConflictsAsync(userId, request, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Creates an owned sequential resumable staging session.</summary>
    [HttpPost]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Create([FromBody] CreateUploadReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await uploads.CreateUploadAsync(userId, request, cancellationToken);
        return this.ToApiResult(result, StatusCodes.Status201Created);
    }

    /// <summary>Returns the owned session's authoritative progress so a durable browser queue can resume safely.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await uploads.GetUploadSessionAsync(userId, id, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Appends exactly the next sequential chunk.</summary>
    [HttpPatch("{id:guid}")]
    [RequireAntiforgeryToken]
    [Consumes("application/octet-stream")]
    public async Task<IActionResult> Append(Guid id, [FromHeader(Name = "Content-Range")] string? contentRange, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        if (!TryParseRange(contentRange, out var start, out var end, out var total))
            return BadRequest(ApiRes.BadRequest("Content-Range must use the form bytes start-end/total."));

        var result = await uploads.AppendUploadChunkAsync(userId, id, start, end, total, Request.Body, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Verifies bytes, stores the immutable original, and commits the catalog item.</summary>
    /// <remarks>Post-commit cache seeding, metadata extraction, recovery manifest creation, and staging cleanup are restart-safe background work.</remarks>
    [HttpPost("{id:guid}/complete")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Complete(Guid id, [FromBody] CompleteUploadReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await uploads.CompleteUploadAsync(userId, id, request, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Cancels an owned pending session and removes its staged bytes.</summary>
    [HttpDelete("{id:guid}")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await uploads.CancelUploadAsync(userId, id, cancellationToken);
        return result.Match<IActionResult>(
            onSuccess: () => NoContent(),
            onFailure: error => this.ToApiError(error));
    }

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static bool TryParseRange(string? value, out long start, out long end, out long total)
    {
        start = end = total = 0;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase)) return false;
        var components = value[6..].Split(['-', '/'], StringSplitOptions.None);
        return components.Length == 3
            && long.TryParse(components[0], out start)
            && long.TryParse(components[1], out end)
            && long.TryParse(components[2], out total)
            && start >= 0 && end >= start && total > 0;
    }
}
