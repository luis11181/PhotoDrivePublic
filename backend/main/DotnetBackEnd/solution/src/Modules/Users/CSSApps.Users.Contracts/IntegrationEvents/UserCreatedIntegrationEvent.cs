using CSSApps.Shared.Contracts.IntegrationEvents;

namespace CSSApps.Users.Contracts.IntegrationEvents;

public sealed record UserCreatedIntegrationEvent : IIntegrationEvent
{
    public string EventClass => EventClassValues.IntegrationEvent;
    public DateTime OccurredOn { get; init; } = DateTime.UtcNow;

    public required Guid UserId { get; init; }
    public required string Email { get; init; }
}
