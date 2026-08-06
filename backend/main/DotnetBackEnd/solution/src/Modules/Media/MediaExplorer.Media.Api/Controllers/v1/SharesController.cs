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

/// <summary>Manages private family sharing and the virtual Shared with me catalog view.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/shares")]
[Authorize]
public sealed class SharesController(ISharingService sharing) : ControllerBase
{
    /// <summary>Lists direct shared roots or the children of an inherited shared folder.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<BrowseItemsRes>))]
    public async Task<IActionResult> Browse(
        [FromQuery] Guid? parentId,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 100,
        [FromQuery] string type = "all",
        [FromQuery] string sort = "name_asc",
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await sharing.BrowseSharedAsync(userId, parentId, cursor, Math.Clamp(limit, 1, 100), type, sort, cancellationToken));
    }

    /// <summary>Lists non-disabled family accounts that may receive a private share.</summary>
    [HttpGet("recipients")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ShareRecipientsRes>))]
    public async Task<IActionResult> Recipients(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await sharing.ListRecipientsAsync(userId, cancellationToken));
    }

    /// <summary>Lists direct viewer grants for an item owned by the caller.</summary>
    [HttpGet("items/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ItemSharesRes>))]
    public async Task<IActionResult> ItemShares(Guid itemId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await sharing.ListItemSharesAsync(userId, itemId, cancellationToken));
    }

    /// <summary>Creates or refreshes one viewer grant; editor and anonymous links are not supported.</summary>
    [HttpPut("items/{itemId:guid}/{recipientUserId:guid}")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ItemShareRes>))]
    public async Task<IActionResult> SetItemShare(Guid itemId, Guid recipientUserId, [FromBody] SetItemShareReq request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await sharing.SetItemShareAsync(userId, itemId, recipientUserId, request, cancellationToken));
    }

    /// <summary>Immediately revokes one direct grant and its inherited folder access.</summary>
    [HttpDelete("items/{itemId:guid}/{recipientUserId:guid}")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> RevokeItemShare(Guid itemId, Guid recipientUserId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await sharing.RevokeItemShareAsync(userId, itemId, recipientUserId, cancellationToken);
        return result.Match<IActionResult>(() => Ok(ApiRes.Success()), error => this.ToApiError(error));
    }

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
