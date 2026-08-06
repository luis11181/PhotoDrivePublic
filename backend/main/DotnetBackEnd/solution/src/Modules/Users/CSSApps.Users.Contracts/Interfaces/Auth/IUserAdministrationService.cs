using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Users.Contracts.DTOs.Auth;

namespace CSSApps.Users.Contracts.Interfaces.Auth;

public interface IUserAdministrationService
{
    SignInOptionsRes GetSignInOptions();
    Task<Result<IReadOnlyCollection<ManagedUserRes>>> ListUsersAsync(CancellationToken cancellationToken);
    Task<Result<InviteUserRes>> InviteAsync(Guid actorUserId, InviteUserReq request, CancellationToken cancellationToken);
    Task<Result<ManagedUserRes>> UpdateAsync(Guid actorUserId, Guid userId, UpdateManagedUserReq request, CancellationToken cancellationToken);
    Task<Result<IssuedResetCodeRes>> IssuePasswordResetAsync(Guid actorUserId, Guid userId, CancellationToken cancellationToken);
    Task<Result> RevokeSessionsAsync(Guid actorUserId, Guid userId, CancellationToken cancellationToken);
    Task<Result<IReadOnlyCollection<SecurityAuditEventRes>>> ListAuditEventsAsync(CancellationToken cancellationToken);
    Task<Result> ResetPasswordAsync(ResetPasswordReq request, CancellationToken cancellationToken);
    Task<Result<IssuedResetCodeRes>> IssueHostOwnerRecoveryAsync(string email, CancellationToken cancellationToken);
}
