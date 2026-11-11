using CSSApps.Shared.Contracts.DomainCommons.Entities;
using CSSApps.Shared.Contracts.DomainCommons.Primitives;
using CSSApps.Shared.Contracts.Primitives;

namespace CSSApps.Users.Domain.Entities.UserAggregate;

/// <summary>
/// Represents a user session with device tracking information.
/// This is an aggregate root - sessions are managed independently by SessionService
/// and have their own transactional boundary.
/// </summary>
public class UserSession : AuditableEntity, IAggregateRoot
{
    protected UserSession() { } // EF Core constructor

    private UserSession(Guid userId, string ipAddress, DateTime lastActivity, string? userAgent, string? deviceName = null)
    {
        Id = Guid.CreateVersion7();
        UserId = userId;
        UserAgent = userAgent ?? "Unknown";
        IpAddress = ipAddress;
        LastActivity = lastActivity;
        IsActive = true;
        DeviceName = deviceName;
        LoginCount = 1;
        FirstLoginAt = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    
    public Guid UserId { get; private set; }
    
    /// <summary>
    /// Friendly device name if provided
    /// </summary>
    public string? DeviceName { get; private set; }
    
    /// <summary>
    /// IP address from which the session was created
    /// </summary>
    public string IpAddress { get; private set; } = null!;
    
    /// <summary>
    /// User agent string for browser identification
    /// </summary>
    public string UserAgent { get; private set; } = null!;
    
    /// <summary>
    /// Last activity timestamp
    /// </summary>
    public DateTime LastActivity { get; private set; }
    
    /// <summary>
    /// When the session was first created
    /// </summary>
    public DateTime FirstLoginAt { get; private set; }
    
    /// <summary>
    /// Number of times this device has been used for login
    /// </summary>
    public int LoginCount { get; private set; }
    
    /// <summary>
    /// Whether this session is currently active
    /// </summary>
    public bool IsActive { get; private set; }
    
    /// <summary>
    /// When the session was terminated (null if still active)
    /// </summary>
    public DateTime? TerminatedAt { get; private set; }
    
    /// <summary>
    /// Reason for session termination
    /// </summary>
    public string? TerminationReason { get; private set; }

    public static Result<UserSession> Create(Guid userId,
        string ipAddress, string? userAgent, string? deviceName = null)
    {
        if (userId == Guid.Empty)
            return new ResultError("User ID cannot be empty");

        if (string.IsNullOrWhiteSpace(ipAddress))
            return new ResultError("IP address cannot be empty");

        return new UserSession(userId, ipAddress: ipAddress,
            DateTime.UtcNow, userAgent: userAgent, deviceName: deviceName);
    }

    public Result UpdateActivity()
    {
        if (!IsActive)
            return new ResultError("Cannot update activity on an inactive session");

        LastActivity = DateTime.UtcNow;
        LoginCount++;
        return Result.Success();
    }

    public Result Terminate(string reason = "User logout")
    {
        if (!IsActive)
            return new ResultError("Session is already terminated");

        IsActive = false;
        TerminatedAt = DateTime.UtcNow;
        TerminationReason = reason;
        return Result.Success();
    }
}
