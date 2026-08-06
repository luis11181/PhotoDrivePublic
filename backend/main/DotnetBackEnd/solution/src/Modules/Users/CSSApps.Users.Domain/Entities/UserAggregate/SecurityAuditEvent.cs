using CSSApps.Shared.Contracts.DomainCommons.Entities;
using CSSApps.Shared.Contracts.DomainCommons.Primitives;
using CSSApps.Shared.Contracts.Primitives;

namespace CSSApps.Users.Domain.Entities.UserAggregate;

public sealed class SecurityAuditEvent : AuditableEntity, IAggregateRoot
{
    private SecurityAuditEvent()
    {
    }

    private SecurityAuditEvent(string eventType, Guid? actorUserId, Guid? targetUserId, string outcome)
    {
        Id = Guid.CreateVersion7();
        EventType = eventType;
        ActorUserId = actorUserId;
        TargetUserId = targetUserId;
        Outcome = outcome;
        OccurredAt = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public string EventType { get; private set; } = null!;
    public Guid? ActorUserId { get; private set; }
    public Guid? TargetUserId { get; private set; }
    public string Outcome { get; private set; } = null!;
    public DateTime OccurredAt { get; private set; }

    public static Result<SecurityAuditEvent> Create(
        string eventType,
        Guid? actorUserId,
        Guid? targetUserId,
        string outcome = "succeeded")
    {
        if (string.IsNullOrWhiteSpace(eventType)) return new ResultError("Event type is required");
        if (string.IsNullOrWhiteSpace(outcome)) return new ResultError("Outcome is required");
        return new SecurityAuditEvent(eventType, actorUserId, targetUserId, outcome);
    }
}
