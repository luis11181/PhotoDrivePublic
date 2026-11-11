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

/// <summary>Manages library-owned photo albums and private family viewer/collaborator sharing.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/albums")]
[Authorize]
public sealed class AlbumsController(IAlbumService albums, IMediaDeliveryService delivery) : ControllerBase
{
    /// <summary>Lists albums owned by or shared with the authenticated family member.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<AlbumsRes>))]
    public async Task<IActionResult> List([FromQuery] string scope = "all", CancellationToken cancellationToken = default) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await albums.ListAsync(userId, scope, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Returns one accessible album and a filtered, sorted page of its active members.</summary>
    [HttpGet("{albumId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<AlbumDetailsRes>))]
    public async Task<IActionResult> Get(Guid albumId, [FromQuery] BrowseAlbumItemsReq request, CancellationToken cancellationToken = default) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await albums.GetAsync(userId, albumId, request with { Limit = Math.Clamp(request.Limit, 1, 100) }, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Streams every active member of an accessible album as a ZIP without retaining a server-side archive.</summary>
    [HttpGet("{albumId:guid}/download")]
    public async Task<IActionResult> Download(Guid albumId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await delivery.PrepareAlbumDownloadAsync(userId, albumId, cancellationToken);
        return await result.Match<Task<IActionResult>>(
            onSuccess: async download =>
            {
                Response.ContentType = download.ContentType;
                Response.Headers.CacheControl = "private, no-store";
                Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(download.FileName)}";
                await download.WriteToAsync(Response.Body, cancellationToken);
                return new EmptyResult();
            },
            onFailure: error => Task.FromResult(this.ToApiError(error)));
    }

    /// <summary>Returns the active member count and source-byte total for an album ZIP.</summary>
    [HttpGet("{albumId:guid}/download/preview")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<DownloadPreviewRes>))]
    public async Task<IActionResult> DownloadPreview(Guid albumId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await delivery.PreviewAlbumDownloadAsync(userId, albumId, cancellationToken));
    }

    /// <summary>Creates an empty album in the caller's library.</summary>
    [HttpPost]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<AlbumRes>))]
    public async Task<IActionResult> Create([FromBody] CreateAlbumReq request, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await albums.CreateAsync(userId, request, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Renames an owned album and changes its optional description.</summary>
    [HttpPatch("{albumId:guid}")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<AlbumRes>))]
    public async Task<IActionResult> Update(Guid albumId, [FromBody] UpdateAlbumReq request, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await albums.UpdateAsync(userId, albumId, request, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Deletes only the album, its memberships, and grants; files and originals are retained.</summary>
    [HttpDelete("{albumId:guid}")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Delete(Guid albumId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return (await albums.DeleteAsync(userId, albumId, cancellationToken)).Match<IActionResult>(
            () => Ok(ApiRes.Success()), error => this.ToApiError(error));
    }

    /// <summary>Lets an owner or collaborator idempotently add an active image/video from their own library.</summary>
    [HttpPut("{albumId:guid}/items/{fileId:guid}")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> AddItem(Guid albumId, Guid fileId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return (await albums.AddItemAsync(userId, albumId, fileId, cancellationToken)).Match<IActionResult>(
            () => Ok(ApiRes.Success()), error => this.ToApiError(error));
    }

    /// <summary>Lets an owner or collaborator idempotently remove any member without changing its file.</summary>
    [HttpDelete("{albumId:guid}/items/{fileId:guid}")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> RemoveItem(Guid albumId, Guid fileId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return (await albums.RemoveItemAsync(userId, albumId, fileId, cancellationToken)).Match<IActionResult>(
            () => Ok(ApiRes.Success()), error => this.ToApiError(error));
    }

    /// <summary>Lets an owner or collaborator transactionally add/remove up to 300 members with per-item outcomes.</summary>
    [HttpPost("{albumId:guid}/items/batch")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<BatchItemsRes>))]
    public async Task<IActionResult> SetItems(Guid albumId, [FromBody] SetAlbumItemsReq request, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await albums.SetItemsAsync(userId, albumId, request, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Lists direct viewer and collaborator grants for an album owned by the caller.</summary>
    [HttpGet("{albumId:guid}/shares")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<AlbumSharesRes>))]
    public async Task<IActionResult> Shares(Guid albumId, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await albums.ListSharesAsync(userId, albumId, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Creates or changes a private family viewer/collaborator grant for an owned album.</summary>
    [HttpPut("{albumId:guid}/shares/{recipientUserId:guid}")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ItemShareRes>))]
    public async Task<IActionResult> SetShare(Guid albumId, Guid recipientUserId, [FromBody] SetItemShareReq request, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await albums.SetShareAsync(userId, albumId, recipientUserId, request, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Immediately revokes a recipient's album and member access.</summary>
    [HttpDelete("{albumId:guid}/shares/{recipientUserId:guid}")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> RevokeShare(Guid albumId, Guid recipientUserId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return (await albums.RevokeShareAsync(userId, albumId, recipientUserId, cancellationToken)).Match<IActionResult>(
            () => Ok(ApiRes.Success()), error => this.ToApiError(error));
    }

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
