namespace CSSApps.Shared.Contracts.IntegrationEvents;

using CSSApps.Shared.Contracts.Primitives;

public interface IIntegrationEventPublisher
{
    Task<Result> PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent;

    Task PublishQueuedAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent;
}
