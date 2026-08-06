using CSSApps.Shared.Contracts.IntegrationEvents;
using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Users.Contracts.IntegrationEvents;

namespace CSSApps.Users.Application.IntegrationEventHandlers;

internal sealed class UserCreatedIntegrationEventHandler
    : IIntegrationEventHandler<UserCreatedIntegrationEvent>
{
    public Task<Result> HandleAsync(
        UserCreatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result.Success());
    }
}
