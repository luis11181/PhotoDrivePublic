using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Application.Services.Auth.OAuth.Google;
using CSSApps.Users.Application.Services.Auth.Password;
using CSSApps.Users.Application.Services.Auth.Session;
using CSSApps.Users.Contracts.DTOs.Auth;
using CSSApps.Users.Contracts.Interfaces.Auth;
using CSSApps.Users.Domain.Entities.UserAggregate;
using CSSApps.Users.Domain.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using Microsoft.Extensions.Options;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Contracts.Constants;

namespace CSSApps.Users.Application.Services.Auth;

/// <summary>
/// Main authentication service handling login, logout, and token operations.
/// 
/// Authentication Flow:
/// 1. User logs in via Login() or GoogleLoginOrRegister()
/// 2. System generates access token (JWT stored in HttpOnly cookie)
/// 3. A rotating refresh credential is generated for the browser session
/// 4. Access token is validated by middleware for subsequent requests
/// </summary>
internal sealed class AuthServices : ILocalAuthenticationServices
{
    private readonly JwtAuth.JwtAuth _jwtAuthenticationServices;
    private readonly ILogger<AuthServices> _logger;
    private readonly IUsersDbContext _dbContext;
    private readonly PasswordService _passwordService;
    private readonly SessionService _sessionService;
    private readonly GoogleOAuthService _googleOAuthService;
    private readonly IFeatureManager _featureManager;
    private readonly FamilyAuthenticationOptions _familyOptions;
    private const string RefreshTokensFeature = "RefreshTokens";

    public AuthServices(
        ILogger<AuthServices> logger,
        JwtAuth.JwtAuth jwtAuthenticationServices,
        IUsersDbContext dbContext,
        PasswordService passwordService,
        SessionService sessionService,
        GoogleOAuthService googleOAuthService,
        IFeatureManager featureManager,
        IOptions<FamilyAuthenticationOptions> familyOptions)
    {
        _jwtAuthenticationServices = jwtAuthenticationServices;
        _logger = logger;
        _dbContext = dbContext;
        _passwordService = passwordService;
        _sessionService = sessionService;
        _googleOAuthService = googleOAuthService;
        _featureManager = featureManager;
        _familyOptions = familyOptions.Value;
    }

    #region Login Methods

    public async Task<Result<AuthRes>> Login(LoginReq model, string ipAddress, string userAgent)
    {
        try
        {
            if (!_familyOptions.PasswordLoginEnabled) return AuthErrors.InvalidCredentials;
            var userResult = await ValidateCredentialsAsync(model);
            if (userResult.IsFailure)
                return userResult.ErrorValue;

            return await CompleteLoginAsync(userResult.SuccessValue, ipAddress, userAgent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.auth] password sign-in failed unexpectedly");
            return AuthErrors.InvalidCredentials;
        }
    }

    /// <summary>
    /// Authenticates an allowlisted user with a Google Identity Services credential.
    /// The first verified sign-in binds Google's stable subject to the invited account.
    /// </summary>
    public async Task<Result<AuthRes>> GoogleLoginOrRegister(string credential, string ipAddress, string userAgent)
    {
        try
        {
            var userResult = await _googleOAuthService.AuthenticateAsync(credential);
            if (userResult.IsFailure)
                return userResult.ErrorValue;

            var authenticatedUser = userResult.SuccessValue;

            var loginResult = await CompleteLoginAsync(
                authenticatedUser.User,
                ipAddress,
                userAgent,
                "Google");

            if (loginResult.IsFailure)
                return loginResult.ErrorValue;

            return loginResult.SuccessValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.auth] google sign-in failed unexpectedly");
            return AuthErrors.OAuthAuthenticationFailed;
        }
    }

    /// <summary>
    /// Common login completion logic for both password and OAuth login.
    /// Creates session, generates tokens, and returns auth response.
    /// </summary>
    private async Task<Result<AuthRes>> CompleteLoginAsync(User user, string ipAddress, string userAgent, string? oauthProvider = null)
    {
        // Create or update session
        var sessionResult = await _sessionService.CreateOrUpdateSessionAsync(
            user.Id,
            ipAddress: ipAddress,
            userAgent: userAgent,
            deviceName: null);

        if (sessionResult.IsFailure)
        {
            _logger.LogError("[users.auth] session creation failed userId={UserId} reason={Reason}",
                user.Id, sessionResult.ErrorValue.Message);
            return sessionResult.ErrorValue;
        }

        var session = sessionResult.SuccessValue;

        // Generate access token (always required)
        var accessTokenResult = await _jwtAuthenticationServices.GenerateAccessTokenAsync(user, session);
        if (accessTokenResult.IsFailure)
        {
            _logger.LogError("[users.auth] access token generation failed userId={UserId} reason={Reason}",
                user.Id, accessTokenResult.ErrorValue.Message);
            return accessTokenResult.ErrorValue;
        }

        string? refreshToken = null;
        if (await _featureManager.IsEnabledAsync(RefreshTokensFeature))
        {
            var refreshTokenResult = await _jwtAuthenticationServices.GenerateRefreshTokenAsync(user, session, oauthProvider);
            if (refreshTokenResult.IsFailure)
            {
                _logger.LogError("[users.auth] refresh token generation failed userId={UserId} reason={Reason}",
                    user.Id, refreshTokenResult.ErrorValue.Message);
                return refreshTokenResult.ErrorValue;
            }
            refreshToken = refreshTokenResult.SuccessValue;
        }

        _logger.LogInformation("[users.auth] sign-in completed userId={UserId} sessionId={SessionId}",
            user.Id, session.Id);

        await _dbContext.SaveChangesAsync();

        return new AuthRes
        {
            Id = user.Id,
            FirstName = user.Name.FirstName,
            LastName = user.Name.LastName,
            Username = user.Email,
            JwtToken = accessTokenResult.SuccessValue,
            RefreshToken = refreshToken,
            User = ToCurrentUser(user)
        };
    }

    #endregion

    #region Credential Validation

    private async Task<Result<User>> ValidateCredentialsAsync(LoginReq model)
    {
        if (string.IsNullOrWhiteSpace(model.Email))
            return AuthErrors.InvalidCredentials;

        var normalizedEmail = model.Email.Trim().ToLowerInvariant();
        var user = await _dbContext.Users
            .Include(candidate => candidate.Roles)
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        if (user == null)
        {
            _logger.LogInformation("[users.auth] password sign-in rejected reason=user_not_found");
            return AuthErrors.InvalidCredentials;
        }

        if (!user.CanUse(LoginMethod.Password) ||
            string.IsNullOrWhiteSpace(model.Password) ||
            !PasswordService.IsSupportedHash(user.PasswordHash))
        {
            _logger.LogInformation("[users.auth] password sign-in rejected userId={UserId} reason=method_not_available", user.Id);
            return AuthErrors.InvalidCredentials;
        }

        var passwordVerification = _passwordService.VerifyPassword(model.Password, user.PasswordHash!);
        if (passwordVerification.IsFailure || !passwordVerification.SuccessValue)
        {
            _logger.LogInformation("[users.auth] password sign-in rejected userId={UserId} reason=invalid_password", user.Id);
            return AuthErrors.InvalidCredentials;
        }

        if (user.MustChangePassword)
        {
            var consumeResult = user.ConsumeInitialPassword();
            if (consumeResult.IsFailure) return AuthErrors.InvalidCredentials;
        }
        user.VerifyEmail();
        user.Activate();
        return user;
    }

    #endregion

    #region Password Management

    public async Task<Result<CurrentUserRes>> SetPassword(Guid userId, SetPasswordReq model)
    {
        try
        {
            var user = await _dbContext.Users
                .Include(candidate => candidate.Roles)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
            {
                _logger.LogInformation("[users.auth] set password rejected userId={UserId} reason=user_not_found", userId);
                return new ResultError("User not found");
            }

            if (!_familyOptions.PasswordLoginEnabled || !user.CanUse(LoginMethod.Password))
                return new ResultError("Password sign-in is not enabled for this account");

            var hadUsablePassword = PasswordService.IsSupportedHash(user.PasswordHash);
            if (hadUsablePassword && !user.MustChangePassword)
            {
                if (string.IsNullOrWhiteSpace(model.CurrentPassword))
                    return new ResultError("Current password is required");
                var currentPassword = _passwordService.VerifyPassword(model.CurrentPassword, user.PasswordHash!);
                if (currentPassword.IsFailure || !currentPassword.SuccessValue)
                    return new ResultError("Current password is incorrect");
            }

            var passwordStrengthResult = PasswordService.IsPasswordStrong(model.Password);
            if (passwordStrengthResult.IsFailure)
                return passwordStrengthResult.ErrorValue;

            var hashedPasswordResult = _passwordService.HashPassword(model.Password);
            if (hashedPasswordResult.IsFailure)
                return hashedPasswordResult.ErrorValue;

            var setPasswordResult = user.SetPassword(hashedPasswordResult.SuccessValue);
            if (setPasswordResult.IsFailure)
                return setPasswordResult.ErrorValue;

            user.VerifyEmail();
            user.Activate();
            var sessions = await _dbContext.UserSessions
                .Where(session => session.UserId == user.Id && session.IsActive)
                .ToListAsync();
            foreach (var session in sessions) session.Terminate("Password changed");
            var tokens = await _dbContext.UserTokens
                .Where(token => token.UserId == user.Id && !token.IsRevoked)
                .ToListAsync();
            foreach (var token in tokens) token.Revoke();
            var auditResult = SecurityAuditEvent.Create(
                hadUsablePassword ? "password.changed" : "password.created",
                user.Id,
                user.Id);
            if (auditResult.IsSuccess) _dbContext.SecurityAuditEvents.Add(auditResult.SuccessValue);

            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("[users.auth] set password completed userId={UserId}", user.Id);

            return ToCurrentUser(user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.auth] set password failed userId={UserId}", userId);
            return new ResultError("Failed to set password");
        }
    }

    public async Task<Result<CurrentUserRes>> GetCurrentUser(Guid userId)
    {
        var user = await _dbContext.Users
            .Include(candidate => candidate.Roles)
            .FirstOrDefaultAsync(candidate => candidate.Id == userId);
        return user is null ? new ResultError("User not found") : ToCurrentUser(user);
    }

    private static CurrentUserRes ToCurrentUser(User user)
    {
        return new CurrentUserRes
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.Name.FullName,
            HasPassword = PasswordService.IsSupportedHash(user.PasswordHash),
            EmailVerified = user.IsEmailVerified,
            IsOwner = user.Roles.Any(role => role.Name == Roles.Owner),
            GoogleLoginAllowed = user.CanUse(LoginMethod.Google),
            PasswordLoginAllowed = user.CanUse(LoginMethod.Password),
            MustChangePassword = user.MustChangePassword
        };
    }

    #endregion

    #region Refresh Token

    /// <summary>
    /// Refreshes the access token using a valid refresh token.
    /// Rotation consumes the old credential atomically. Reuse of a consumed credential revokes
    /// every refresh credential in the session and terminates that session.
    /// </summary>
    public async Task<Result<AuthRes>> RefreshToken(RefreshTokenReq model)
    {
        try
        {
            if (!await _featureManager.IsEnabledAsync(RefreshTokensFeature))
                return new ResultError("Refresh tokens are disabled");

            if (string.IsNullOrWhiteSpace(model.RefreshToken))
                return new ResultError("Refresh token is required");

            await using var transaction = await _dbContext.BeginTransactionAsync();
            var userSessionResult = await _jwtAuthenticationServices.ValidateRefreshTokenAsync(model.RefreshToken);
            if (userSessionResult.IsFailure)
            {
                await _dbContext.RollbackTransactionAsync();
                return userSessionResult.ErrorValue;
            }

            var validated = userSessionResult.SuccessValue;
            var user = validated.User;
            var session = validated.Session;

            if (validated.Token.IsRevoked)
            {
                await _jwtAuthenticationServices.RevokeRefreshTokenFamilyForReplayAsync(session.Id);
                await _dbContext.SaveChangesAsync();
                await _dbContext.CommitTransactionAsync();
                _logger.LogWarning("[users.auth] refresh token replay terminated session userId={UserId} sessionId={SessionId}",
                    user.Id, session.Id);
                return new ResultError("Invalid refresh token");
            }

            if (validated.Token.IsExpired || !session.IsActive)
            {
                await _dbContext.RollbackTransactionAsync();
                return new ResultError("Invalid refresh token");
            }

            if (!await _jwtAuthenticationServices.TryConsumeRefreshTokenAsync(validated.Token.Id))
            {
                await _jwtAuthenticationServices.RevokeRefreshTokenFamilyForReplayAsync(session.Id);
                await _dbContext.SaveChangesAsync();
                await _dbContext.CommitTransactionAsync();
                _logger.LogWarning("[users.auth] concurrent refresh token replay terminated session userId={UserId} sessionId={SessionId}",
                    user.Id, session.Id);
                return new ResultError("Invalid refresh token");
            }

            var activityResult = session.UpdateActivity();
            if (activityResult.IsFailure)
            {
                await _dbContext.RollbackTransactionAsync();
                return activityResult.ErrorValue;
            }

            // Generate new tokens
            var accessTokenResult = await _jwtAuthenticationServices.GenerateAccessTokenAsync(user, session);
            if (accessTokenResult.IsFailure)
            {
                _logger.LogError("[users.auth] access token refresh generation failed userId={UserId} reason={Reason}",
                    user.Id, accessTokenResult.ErrorValue.Message);
                return accessTokenResult.ErrorValue;
            }

            var newRefreshTokenResult = await _jwtAuthenticationServices.GenerateRefreshTokenAsync(user, session);
            if (newRefreshTokenResult.IsFailure)
                return newRefreshTokenResult.ErrorValue;

            await _dbContext.SaveChangesAsync();
            await _dbContext.CommitTransactionAsync();

            _logger.LogInformation("[users.auth] token refresh completed userId={UserId} sessionId={SessionId}",
                user.Id, session.Id);

            return new AuthRes
            {
                Id = user.Id,
                FirstName = user.Name.FirstName,
                LastName = user.Name.LastName,
                Username = user.Email,
                JwtToken = accessTokenResult.SuccessValue,
                RefreshToken = newRefreshTokenResult.SuccessValue,
                User = ToCurrentUser(user)
            };
        }
        catch (Exception ex)
        {
            await _dbContext.RollbackTransactionAsync();
            _logger.LogError(ex, "[users.auth] token refresh failed unexpectedly");
            return new ResultError("Failed to refresh token");
        }
    }

    #endregion

    #region Logout

    public async Task<Result> Logout(Guid userId, Guid sessionId, bool logoutFromAllDevices)
    {
        try
        {
            if (logoutFromAllDevices)
            {
                await _jwtAuthenticationServices.RevokeAllUserRefreshTokensAsync(userId, "Logout from all devices");
                await _sessionService.TerminateAllUserSessionsAsync(userId, "Logout from all devices");
                _logger.LogInformation("[users.auth] logout all devices completed userId={UserId}", userId);
            }
            else
            {
                var session = await _dbContext.UserSessions
                    .FirstOrDefaultAsync(candidate => candidate.Id == sessionId && candidate.UserId == userId);
                if (session is null) return new ResultError("Session not found");
                await _jwtAuthenticationServices.RevokeSessionRefreshTokensAsync(sessionId, "User logout");
                if (session.IsActive) session.Terminate("User logout");
                _logger.LogInformation("[users.auth] logout completed userId={UserId} sessionId={SessionId}", userId, sessionId);
            }

            await _dbContext.SaveChangesAsync();

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.auth] logout failed unexpectedly");
            return new ResultError("Failed to logout");
        }
    }

    #endregion
}
