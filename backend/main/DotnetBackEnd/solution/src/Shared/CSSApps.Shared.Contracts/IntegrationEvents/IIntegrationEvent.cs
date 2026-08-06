namespace CSSApps.Shared.Contracts.IntegrationEvents;

/// <summary>
///     Represents an event that is meant to go accross different modules in the system.
/// </summary>
public interface IIntegrationEvent
{
    string EventClass { get; }

    //Guid EventId { get; }
    DateTime OccurredOn { get; }
}

public static class EventClassValues
{
    public static readonly string IntegrationEvent = nameof(IntegrationEvent);
    // Add other event classes here
}