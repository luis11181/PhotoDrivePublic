using System.Security.Claims;
using Asp.Versioning;
using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Utils.ApiHelpers.Filters;
using CSSApps.Users.Contracts.DTOs.Auth;
using CSSApps.Users.Contracts.Interfaces.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CSSApps.Users.Api.Controllers.v1;

[ApiController]
[ApiVersion("1.0")]
[Tags("Users - Family administration")]
[Route("api/v{version:apiVersion}/users")]
public sealed class UsersAdministrationController(IUserAdministrationService service) : ControllerBase
{
    /// <summary>Returns only the globally available sign-in methods; it never reveals account-specific admission.</summary>
    [AllowAnonymous]
    [HttpGet("sign-in-options")]
    public IActionResult SignInOptions() => Ok(ApiRes<SignInOptionsRes>.Success(service.GetSignInOptions()));

    /// <summary>Consumes a single-use setup/reset/recovery code and revokes every existing session.</summary>
    [AllowAnonymous]
    [HttpPost("reset-password")]
    [EnableRateLimiting("StrictPolicy")]
    public async Task<IActionResult> ResetPassword(ResetPasswordReq request, CancellationToken cancellationToken)
    {
        var result = await service.ResetPasswordAsync(request, cancellationToken);
        return result.IsSuccess
            ? Ok(ApiRes.Success())
            : BadRequest(ApiRes.BadRequest("Reset code is invalid or expired"));
    }

    [Authorize(Policy = "Owner")]
    [HttpGet("admin")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await service.ListUsersAsync(cancellationToken);
        return result.IsSuccess
            ? Ok(ApiRes<IReadOnlyCollection<ManagedUserRes>>.Success(result.SuccessValue))
            : BadRequest(ApiRes.BadRequest(result.ErrorValue.Message));
    }

    [Authorize(Policy = "Owner")]
    [RequireAntiforgeryToken]
    [HttpPost("admin")]
    public async Task<IActionResult> Invite(InviteUserReq request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor)) return Unauthorized(ApiRes.Unauthorized());
        var result = await service.InviteAsync(actor, request, cancellationToken);
        return result.IsSuccess
            ? Ok(ApiRes<InviteUserRes>.Success(result.SuccessValue))
            : BadRequest(ApiRes.BadRequest(result.ErrorValue.Message));
    }

    [Authorize(Policy = "Owner")]
    [RequireAntiforgeryToken]
    [HttpPatch("admin/{userId:guid}")]
    public async Task<IActionResult> Update(Guid userId, UpdateManagedUserReq request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor)) return Unauthorized(ApiRes.Unauthorized());
        var result = await service.UpdateAsync(actor, userId, request, cancellationToken);
        return result.IsSuccess
            ? Ok(ApiRes<ManagedUserRes>.Success(result.SuccessValue))
            : BadRequest(ApiRes.BadRequest(result.ErrorValue.Message));
    }

    [Authorize(Policy = "Owner")]
    [RequireAntiforgeryToken]
    [HttpPost("admin/{userId:guid}/password-reset")]
    public async Task<IActionResult> IssuePasswordReset(Guid userId, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor)) return Unauthorized(ApiRes.Unauthorized());
        var result = await service.IssuePasswordResetAsync(actor, userId, cancellationToken);
        return result.IsSuccess
            ? Ok(ApiRes<IssuedResetCodeRes>.Success(result.SuccessValue))
            : BadRequest(ApiRes.BadRequest(result.ErrorValue.Message));
    }

    [Authorize(Policy = "Owner")]
    [RequireAntiforgeryToken]
    [HttpPost("admin/{userId:guid}/revoke-sessions")]
    public async Task<IActionResult> RevokeSessions(Guid userId, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor)) return Unauthorized(ApiRes.Unauthorized());
        var result = await service.RevokeSessionsAsync(actor, userId, cancellationToken);
        return result.IsSuccess ? Ok(ApiRes.Success()) : BadRequest(ApiRes.BadRequest(result.ErrorValue.Message));
    }

    [Authorize(Policy = "Owner")]
    [HttpGet("admin/audit-events")]
    public async Task<IActionResult> AuditEvents(CancellationToken cancellationToken)
    {
        var result = await service.ListAuditEventsAsync(cancellationToken);
        return result.IsSuccess
            ? Ok(ApiRes<IReadOnlyCollection<SecurityAuditEventRes>>.Success(result.SuccessValue))
            : BadRequest(ApiRes.BadRequest(result.ErrorValue.Message));
    }

    private bool TryGetActor(out Guid actor) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out actor);
}
