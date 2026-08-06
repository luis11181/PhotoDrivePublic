using CSSApps.Shared.Contracts.IntegrationEvents;

namespace CSSApps.Users.Contracts.IntegrationEvents;

public class CustomerCreated : IIntegrationEvent
{
    public Guid CommandId { get; init; }
    public DateTime Timestamp { get; init; }
    public string EventClass => EventClassValues.IntegrationEvent;

    public DateTime OccurredOn { get; init; }
}

public record UpdateCustomerAddress : IIntegrationEvent
{
    public Guid CommandId { get; init; }
    public DateTime Timestamp { get; init; }

    public string EventClass => EventClassValues.IntegrationEvent;

    public DateTime OccurredOn { get; init; }
}