using System.Security.Cryptography;
using System.Text;
using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Application.Services.Auth.Password;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Contracts.Constants;
using CSSApps.Users.Contracts.DTOs.Auth;
using CSSApps.Users.Contracts.Interfaces.Auth;
using CSSApps.Users.Domain.Entities.UserAggregate;
using CSSApps.Users.Domain.Entities.UserAggregate.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSSApps.Users.Application.Services.Auth;

internal sealed class UserAdministrationService(
    IUsersDbContext dbContext,
    PasswordService passwordService,
    IUserPermissionsService permissionsService,
    IOptions<FamilyAuthenticationOptions> familyOptions,
    IOptions<GoogleOAuthConfig> googleOptions,
    TimeProvider timeProvider) : IUserAdministrationService
{
    private static readonly SemaphoreSlim OwnerMutationGate = new(1, 1);
    private readonly FamilyAuthenticationOptions _family = familyOptions.Value;
    private readonly GoogleOAuthConfig _google = googleOptions.Value;

    public SignInOptionsRes GetSignInOptions() => new()
    {
        GoogleEnabled = _google.IsConfigured,
        PasswordEnabled = _family.PasswordLoginEnabled,
        GoogleClientId = _google.IsConfigured ? _google.ClientId : null
    };

    public async Task<Result<IReadOnlyCollection<ManagedUserRes>>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .AsNoTracking()
            .Include(user => user.Roles)
            .Where(user => !user.IsServiceAccount)
            .OrderBy(user => user.Email)
            .ToListAsync(cancellationToken);
        var sessionCounts = await dbContext.UserSessions
            .AsNoTracking()
            .Where(session => session.IsActive)
            .GroupBy(session => session.UserId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.UserId, row => row.Count, cancellationToken);

        return users.Select(user => ToManagedUser(user, sessionCounts.GetValueOrDefault(user.Id))).ToArray();
    }

    /// <summary>
    /// Adds an allowlisted family account. Creating the record is the invitation:
    /// there is no public self-signup path, and password users receive a one-time setup code.
    /// </summary>
    public async Task<Result<InviteUserRes>> InviteAsync(
        Guid actorUserId,
        InviteUserReq request,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        if (await dbContext.Users.AnyAsync(user => user.Email == email, cancellationToken))
            return new ResultError("An account with this email already exists");

        var accessResult = ResolveAccess(request.Role, request.GoogleAllowed, request.PasswordAllowed);
        if (accessResult.IsFailure) return accessResult.ErrorValue;
        var (role, methods) = accessResult.SuccessValue;

        var createResult = User.CreateInvited(email, ParseName(request.DisplayName));
        if (createResult.IsFailure) return createResult.ErrorValue;
        var user = createResult.SuccessValue;
        var access = user.ConfigureAccess(methods, HumanAccountStatus.Invited);
        if (access.IsFailure) return access.ErrorValue;
        var roleResult = user.SetHumanRole(role);
        if (roleResult.IsFailure) return roleResult.ErrorValue;
        dbContext.Users.Add(user);

        IssuedResetCodeRes? passwordSetup = null;
        if (methods.HasFlag(LoginMethod.Password))
        {
            passwordSetup = IssueToken(user.Id, SecurityTokenPurpose.PasswordSetup, actorUserId, expires: true);
        }

        AddAudit("user.invited", actorUserId, user.Id);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new InviteUserRes { User = ToManagedUser(user, 0), PasswordSetup = passwordSetup };
    }

    /// <summary>
    /// Changes a human account's role, status, and permitted sign-in methods.
    /// Owner mutations are serialized so two concurrent requests cannot both remove the last owner.
    /// </summary>
    public async Task<Result<ManagedUserRes>> UpdateAsync(
        Guid actorUserId,
        Guid userId,
        UpdateManagedUserReq request,
        CancellationToken cancellationToken)
    {
        await OwnerMutationGate.WaitAsync(cancellationToken);
        try
        {
            return await UpdateCoreAsync(actorUserId, userId, request, cancellationToken);
        }
        finally
        {
            OwnerMutationGate.Release();
        }
    }

    private async Task<Result<ManagedUserRes>> UpdateCoreAsync(
        Guid actorUserId,
        Guid userId,
        UpdateManagedUserReq request,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.Include(candidate => candidate.Roles)
            .FirstOrDefaultAsync(candidate => candidate.Id == userId && !candidate.IsServiceAccount, cancellationToken);
        if (user is null) return new ResultError("User not found");

        if (!Enum.TryParse<HumanAccountStatus>(request.Status, true, out var status))
            return new ResultError("Status must be Invited, Active, or Disabled");

        var accessResult = ResolveAccess(request.Role, request.GoogleAllowed, request.PasswordAllowed);
        if (accessResult.IsFailure) return accessResult.ErrorValue;
        var (role, methods) = accessResult.SuccessValue;

        var wasOwner = user.Roles.Any(existingRole => existingRole.Name == Roles.Owner);
        var remainsOwner = role.Name == Roles.Owner && status != HumanAccountStatus.Disabled;
        if (wasOwner && !remainsOwner && !await HasAnotherOwnerAsync(user.Id, cancellationToken))
            return new ResultError("The last active owner cannot be disabled or changed to Member");

        var contactResult = user.UpdateContactInfo(name: ParseName(request.DisplayName));
        if (contactResult.IsFailure) return contactResult.ErrorValue;
        var access = user.ConfigureAccess(methods, status);
        if (access.IsFailure) return access.ErrorValue;
        if (!methods.HasFlag(LoginMethod.Password))
        {
            user.RemovePassword();
            InvalidateTokens(user.Id, SecurityTokenPurpose.PasswordSetup, SecurityTokenPurpose.PasswordReset);
        }
        var roleResult = user.SetHumanRole(role);
        if (roleResult.IsFailure) return roleResult.ErrorValue;

        await RevokeCredentialsAsync(user, "Account access changed", cancellationToken);
        AddAudit("user.updated", actorUserId, user.Id);
        await dbContext.SaveChangesAsync(cancellationToken);
        await permissionsService.InvalidateUserPermissionsAsync(user.Id);

        var activeSessions = await dbContext.UserSessions.CountAsync(
            session => session.UserId == user.Id && session.IsActive, cancellationToken);
        return ToManagedUser(user, activeSessions);
    }

    /// <summary>
    /// Replaces any outstanding password setup/reset code with a short-lived, single-use code.
    /// The plaintext code is returned once for the owner to deliver out of band.
    /// </summary>
    public async Task<Result<IssuedResetCodeRes>> IssuePasswordResetAsync(
        Guid actorUserId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(
            candidate => candidate.Id == userId && !candidate.IsServiceAccount, cancellationToken);
        if (user is null) return new ResultError("User not found");
        if (!_family.PasswordLoginEnabled || !user.CanUse(LoginMethod.Password))
            return new ResultError("Password sign-in is not enabled for this account");

        InvalidateTokens(user.Id, SecurityTokenPurpose.PasswordReset, SecurityTokenPurpose.PasswordSetup);
        var token = IssueToken(user.Id, SecurityTokenPurpose.PasswordReset, actorUserId, expires: true);
        AddAudit("password.reset_issued", actorUserId, user.Id);
        await dbContext.SaveChangesAsync(cancellationToken);
        return token;
    }

    public async Task<Result> RevokeSessionsAsync(Guid actorUserId, Guid userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(
            candidate => candidate.Id == userId && !candidate.IsServiceAccount, cancellationToken);
        if (user is null) return new ResultError("User not found");
        await RevokeCredentialsAsync(user, "Owner revoked sessions", cancellationToken);
        AddAudit("sessions.revoked", actorUserId, user.Id);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyCollection<SecurityAuditEventRes>>> ListAuditEventsAsync(
        CancellationToken cancellationToken)
    {
        var events = await dbContext.SecurityAuditEvents.AsNoTracking()
            .OrderByDescending(auditEvent => auditEvent.OccurredAt)
            .Take(100)
            .Select(auditEvent => new SecurityAuditEventRes
            {
                Id = auditEvent.Id,
                EventType = auditEvent.EventType,
                ActorUserId = auditEvent.ActorUserId,
                TargetUserId = auditEvent.TargetUserId,
                Outcome = auditEvent.Outcome,
                OccurredAt = auditEvent.OccurredAt
            })
            .ToArrayAsync(cancellationToken);
        return events;
    }

    /// <summary>
    /// Redeems any supported password setup/recovery code, consumes all recovery paths for
    /// the account, and revokes existing sessions before the new password can be used.
    /// </summary>
    public async Task<Result> ResetPasswordAsync(ResetPasswordReq request, CancellationToken cancellationToken)
    {
        var passwordStrength = PasswordService.IsPasswordStrong(request.Password);
        if (passwordStrength.IsFailure) return passwordStrength.ErrorValue;
        var tokenResult = await FindTokenAsync(request.Code, cancellationToken);
        if (tokenResult.IsFailure) return new ResultError("Reset code is invalid or expired");
        var token = tokenResult.SuccessValue;
        if (token.Purpose is not (SecurityTokenPurpose.PasswordSetup or SecurityTokenPurpose.PasswordReset
            or SecurityTokenPurpose.HostOwnerRecovery))
            return new ResultError("Reset code is invalid or expired");

        var user = await dbContext.Users.Include(candidate => candidate.Roles)
            .FirstOrDefaultAsync(candidate => candidate.Id == token.UserId, cancellationToken);
        if (user is null || !_family.PasswordLoginEnabled || !user.CanUse(LoginMethod.Password))
            return new ResultError("Reset code is invalid or expired");
        if (token.Purpose is SecurityTokenPurpose.HostOwnerRecovery &&
            !user.Roles.Any(role => role.Name == Roles.Owner))
            return new ResultError("Reset code is invalid or expired");

        var hash = passwordService.HashPassword(request.Password);
        if (hash.IsFailure) return hash.ErrorValue;
        var set = user.SetPassword(hash.SuccessValue);
        if (set.IsFailure) return set.ErrorValue;
        user.VerifyEmail();
        user.Activate();
        token.Consume(UtcNow);
        await RevokeCredentialsAsync(user, "Password reset", cancellationToken, rotateStamp: false);
        InvalidateTokens(user.Id, SecurityTokenPurpose.PasswordSetup, SecurityTokenPurpose.PasswordReset,
            SecurityTokenPurpose.HostOwnerRecovery);
        AddAudit("password.reset_completed", null, user.Id);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Issues the last-resort owner recovery code used by the local host command.
    /// This service operation is intentionally not exposed through an HTTP endpoint.
    /// </summary>
    public async Task<Result<IssuedResetCodeRes>> IssueHostOwnerRecoveryAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var owner = await dbContext.Users.Include(user => user.Roles)
            .FirstOrDefaultAsync(user => user.Email == NormalizeEmail(email) && !user.IsServiceAccount, cancellationToken);
        if (owner is null || !owner.Roles.Any(role => role.Name == Roles.Owner))
            return new ResultError("Owner account not found");
        if (!_family.PasswordLoginEnabled)
            return new ResultError(
                "Temporarily enable FamilyAuthentication:PasswordLoginEnabled before running host owner recovery");
        if (!owner.CanUse(LoginMethod.Password))
        {
            var status = owner.AccountStatus == HumanAccountStatus.Disabled
                ? HumanAccountStatus.Active
                : owner.AccountStatus;
            var access = owner.ConfigureAccess(owner.AllowedLoginMethods | LoginMethod.Password, status);
            if (access.IsFailure) return access.ErrorValue;
        }

        InvalidateTokens(owner.Id, SecurityTokenPurpose.HostOwnerRecovery);
        var token = IssueToken(owner.Id, SecurityTokenPurpose.HostOwnerRecovery, null, expires: true);
        AddAudit("owner.host_recovery_issued", null, owner.Id);
        await dbContext.SaveChangesAsync(cancellationToken);
        return token;
    }

    private Result<(Role Role, LoginMethod Methods)> ResolveAccess(
        string roleName,
        bool googleAllowed,
        bool passwordAllowed)
    {
        var normalizedRole = roleName.Trim().ToUpperInvariant();
        if (normalizedRole is not (Roles.Owner or Roles.Member))
            return new ResultError("Role must be Owner or Member");
        if (googleAllowed && !_google.IsConfigured) return new ResultError("Google sign-in is not configured");
        if (passwordAllowed && !_family.PasswordLoginEnabled) return new ResultError("Password sign-in is disabled");
        var methods = (googleAllowed ? LoginMethod.Google : LoginMethod.None) |
                      (passwordAllowed ? LoginMethod.Password : LoginMethod.None);
        if (methods == LoginMethod.None) return new ResultError("At least one available sign-in method is required");
        var role = dbContext.Roles.Local.FirstOrDefault(candidate => candidate.Name == normalizedRole)
                   ?? dbContext.Roles.Single(candidate => candidate.Name == normalizedRole);
        return (role, methods);
    }

    private async Task<bool> HasAnotherOwnerAsync(Guid excludedUserId, CancellationToken cancellationToken) =>
        await dbContext.Users.AnyAsync(user =>
            user.Id != excludedUserId &&
            !user.IsServiceAccount &&
            user.AccountStatus != HumanAccountStatus.Disabled &&
            user.Roles.Any(role => role.Name == Roles.Owner), cancellationToken);

    /// <summary>
    /// Creates a high-entropy code while persisting only its SHA-256 hash. The database ID
    /// narrows lookup; the random secret remains necessary to redeem the code.
    /// </summary>
    private IssuedResetCodeRes IssueToken(
        Guid userId,
        SecurityTokenPurpose purpose,
        Guid? actorUserId,
        bool expires)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var tokenResult = SecurityToken.Create(
            userId,
            purpose,
            HashSecret(secret),
            expires ? UtcNow.AddMinutes(_family.PasswordResetLifetimeMinutes) : null,
            actorUserId);
        if (tokenResult.IsFailure) throw new InvalidOperationException(tokenResult.ErrorValue.Message);
        var token = tokenResult.SuccessValue;
        dbContext.SecurityTokens.Add(token);
        return new IssuedResetCodeRes
        {
            Code = $"{token.Id:N}.{secret}",
            ExpiresAt = token.ExpiresAt
        };
    }

    /// <summary>
    /// Resolves a usable token and compares its secret hash in fixed time to avoid leaking
    /// useful information through comparison timing.
    /// </summary>
    private async Task<Result<SecurityToken>> FindTokenAsync(string rawCode, CancellationToken cancellationToken)
    {
        var parts = rawCode.Trim().Split('.', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var tokenId))
            return new ResultError("Invalid token");
        var token = await dbContext.SecurityTokens.FirstOrDefaultAsync(candidate => candidate.Id == tokenId, cancellationToken);
        if (token is null || !token.CanUse(UtcNow) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(token.SecretHash),
                Encoding.ASCII.GetBytes(HashSecret(parts[1]))))
            return new ResultError("Invalid token");
        return token;
    }

    private void InvalidateTokens(Guid userId, params SecurityTokenPurpose[] purposes)
    {
        var purposeSet = purposes.ToHashSet();
        var tokens = dbContext.SecurityTokens
            .Where(token => token.UserId == userId && token.UsedAt == null && purposeSet.Contains(token.Purpose))
            .ToList();
        foreach (var token in tokens) token.Consume(UtcNow);
    }

    /// <summary>
    /// Invalidates both stateless access tokens (through the security stamp) and persisted
    /// session/refresh-token records so access changes take effect immediately.
    /// </summary>
    private async Task RevokeCredentialsAsync(
        User user,
        string reason,
        CancellationToken cancellationToken,
        bool rotateStamp = true)
    {
        if (rotateStamp) user.RevokeSessions();
        var sessions = await dbContext.UserSessions
            .Where(session => session.UserId == user.Id && session.IsActive)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions) session.Terminate(reason);
        var tokens = await dbContext.UserTokens
            .Where(token => token.UserId == user.Id && !token.IsRevoked)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens) token.Revoke();
    }

    private void AddAudit(string eventType, Guid? actorUserId, Guid? targetUserId)
    {
        var result = SecurityAuditEvent.Create(eventType, actorUserId, targetUserId);
        if (result.IsFailure) throw new InvalidOperationException(result.ErrorValue.Message);
        dbContext.SecurityAuditEvents.Add(result.SuccessValue);
    }

    private static ManagedUserRes ToManagedUser(User user, int activeSessions) => new()
    {
        Id = user.Id,
        Email = user.Email,
        DisplayName = user.Name.FullName,
        Role = user.Roles.SingleOrDefault()?.Name ?? Roles.Member,
        Status = user.AccountStatus.ToString(),
        GoogleAllowed = user.AllowedLoginMethods.HasFlag(LoginMethod.Google),
        PasswordAllowed = user.AllowedLoginMethods.HasFlag(LoginMethod.Password),
        GoogleLinked = user.IsOAuthUser(),
        HasPassword = PasswordService.IsSupportedHash(user.PasswordHash),
        MustChangePassword = user.MustChangePassword,
        ActiveSessionCount = activeSessions
    };

    private static PersonName ParseName(string displayName)
    {
        var parts = displayName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return new PersonName { FirstName = parts[0], LastName = parts.Length > 1 ? parts[1] : null };
    }

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;
    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
