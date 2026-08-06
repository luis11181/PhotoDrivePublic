using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSSApps.Users.Application.Services.Auth.Session;

/// <summary>
/// Service for managing user sessions.
///
/// Sessions track user login instances across devices/browsers.
/// Each session has:
/// - User association
/// - IP address and user agent for device identification
/// - Activity timestamps
/// - Active/terminated status
///
/// </summary>
internal sealed class SessionService
{
    private readonly IUsersDbContext _dbContext;
    private readonly ILogger<SessionService> _logger;

    public SessionService(
        IUsersDbContext dbContext,
        ILogger<SessionService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new independently revocable browser session for the user.
    /// </summary>
    public async Task<Result<UserSession>> CreateOrUpdateSessionAsync(
        Guid userId,
        string ipAddress,
        string? userAgent,
        string? deviceName)
    {
        try
        {
            var sessionResult = UserSession.Create(userId, ipAddress: ipAddress, userAgent: userAgent, deviceName: deviceName);
            if (sessionResult.IsFailure)
                return sessionResult.ErrorValue;

            var session = sessionResult.SuccessValue;
            _dbContext.UserSessions.Add(session);

            _logger.LogInformation("[users.session] Created new session {SessionId} for user {UserId}",
                session.Id, userId);

            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.session] Failed to create or update session for user {UserId}", userId);
            return new ResultError($"Failed to create session: {ex.Message}");
        }
    }

    public async Task<Result> TerminateSessionAsync(Guid sessionId, string reason = "User logout")
    {
        try
        {
            var session = await _dbContext.UserSessions.FindAsync(sessionId);
            if (session == null)
                return new ResultError("Session not found");

            var terminateResult = session.Terminate(reason);
            if (terminateResult.IsFailure)
                return terminateResult.ErrorValue;

            _logger.LogInformation("[users.session] Terminated session {SessionId}. Reason: {Reason}", sessionId, reason);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.session] Failed to terminate session {SessionId}", sessionId);
            return new ResultError($"Failed to terminate session: {ex.Message}");
        }
    }

    public async Task<Result> TerminateAllUserSessionsAsync(Guid userId, string reason = "Security action")
    {
        try
        {
            var sessions = await _dbContext.UserSessions
                .Where(s => s.UserId == userId && s.IsActive)
                .ToListAsync();

            foreach (var session in sessions)
            {
                session.Terminate(reason);
            }

            _logger.LogInformation("[users.session] Terminated {Count} sessions for user {UserId}. Reason: {Reason}",
                sessions.Count, userId, reason);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.session] Failed to terminate all sessions for user {UserId}", userId);
            return new ResultError($"Failed to terminate sessions: {ex.Message}");
        }
    }

    public async Task<Result<IEnumerable<UserSession>>> GetActiveUserSessionsAsync(Guid userId)
    {
        try
        {
            var sessions = await _dbContext.UserSessions
                .Where(s => s.UserId == userId && s.IsActive)
                .OrderByDescending(s => s.LastActivity)
                .ToListAsync();

            return Result<IEnumerable<UserSession>>.Success(sessions.AsEnumerable());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.session] Failed to get active sessions for user {UserId}", userId);
            return new ResultError($"Failed to get sessions: {ex.Message}");
        }
    }
}
