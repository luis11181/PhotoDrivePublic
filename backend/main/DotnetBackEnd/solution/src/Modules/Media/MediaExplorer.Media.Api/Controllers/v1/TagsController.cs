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

/// <summary>Manages library-owned catalog tags and their file/folder assignments.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tags")]
[Authorize]
public sealed class TagsController(ITagService tags) : ControllerBase
{
    /// <summary>Lists every tag owned by the authenticated member's library with current file and folder assignment counts.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<IReadOnlyList<TagRes>>))]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await tags.ListAsync(userId, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Creates a case-insensitively unique library tag with a supported display color.</summary>
    [HttpPost]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(ApiRes<TagRes>))]
    public async Task<IActionResult> Create([FromBody] CreateTagReq request, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await tags.CreateAsync(userId, request, cancellationToken), StatusCodes.Status201Created)
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Renames or recolors a tag owned by the authenticated member's library.</summary>
    [HttpPatch("{tagId:guid}")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<TagRes>))]
    public async Task<IActionResult> Update(Guid tagId, [FromBody] UpdateTagReq request, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await tags.UpdateAsync(userId, tagId, request, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    /// <summary>Deletes a library tag and its file/folder assignments without deleting any catalog items or original bytes.</summary>
    [HttpDelete("{tagId:guid}")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid tagId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized(ApiRes.Unauthorized());
        var result = await tags.DeleteAsync(userId, tagId, cancellationToken);
        return result.Match<IActionResult>(
            onSuccess: () => NoContent(),
            onFailure: error => this.ToApiError(error));
    }

    /// <summary>Adds or removes one tag across 1–300 active owned files or ordinary folders and returns one outcome per item.</summary>
    [HttpPost("{tagId:guid}/items/batch")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<BatchItemsRes>))]
    public async Task<IActionResult> SetItems(Guid tagId, [FromBody] SetTagItemsReq request, CancellationToken cancellationToken) =>
        TryGetUserId(out var userId)
            ? this.ToApiResult(await tags.SetItemsAsync(userId, tagId, request, cancellationToken))
            : Unauthorized(ApiRes.Unauthorized());

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
