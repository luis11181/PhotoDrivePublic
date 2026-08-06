namespace CSSApps.Shared.Contracts.IntegrationEvents;

using CSSApps.Shared.Contracts.Primitives;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Integration event handler naming convention")]
public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    Task<Result> HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken = default);
}
