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

/// <summary>Manages library-owned calendar annotations for the Photos timeline.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/timeline-events")]
[Authorize]
public sealed class TimelineEventsController(ITimelineEventService events) : ControllerBase
{
    /// <summary>Lists the caller's events, or returns bounded title/description suggestions.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<TimelineEventsRes>))]
    public async Task<IActionResult> List([FromQuery] string? q = null, [FromQuery] int limit = 500, CancellationToken cancellationToken = default) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await events.ListAsync(userId, q, limit, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Returns one event from the caller's own library.</summary>
    [HttpGet("{eventId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<TimelineEventRes>))]
    public async Task<IActionResult> Get(Guid eventId, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await events.GetAsync(userId, eventId, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Creates a date-only timeline event without materializing photo membership.</summary>
    [HttpPost]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<TimelineEventRes>))]
    public async Task<IActionResult> Create([FromBody] CreateTimelineEventReq request, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await events.CreateAsync(userId, request, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Updates an owned event's title, dates, and optional description.</summary>
    [HttpPatch("{eventId:guid}")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<TimelineEventRes>))]
    public async Task<IActionResult> Update(Guid eventId, [FromBody] UpdateTimelineEventReq request, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await events.UpdateAsync(userId, eventId, request, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Deletes only the event; catalog files and originals are untouched.</summary>
    [HttpDelete("{eventId:guid}")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Delete(Guid eventId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return (await events.DeleteAsync(userId, eventId, cancellationToken)).Match<IActionResult>(
            () => Ok(ApiRes.Success()), error => this.ToApiError(error));
    }

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
