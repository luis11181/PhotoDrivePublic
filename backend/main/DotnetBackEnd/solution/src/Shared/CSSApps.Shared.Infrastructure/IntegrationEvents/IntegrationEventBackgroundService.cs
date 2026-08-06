using System.Threading.Channels;
using CSSApps.Shared.Contracts.IntegrationEvents;
using CSSApps.Shared.Contracts.Primitives;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSSApps.Shared.Infrastructure.IntegrationEvents;

public sealed class IntegrationEventBackgroundService(
    Channel<IIntegrationEvent> channel,
    IIntegrationEventPublisher publisher,
    ILogger<IntegrationEventBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var integrationEvent in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var result = await publisher.PublishAsync(integrationEvent, stoppingToken);
                if (result.IsFailure)
                {
                    logger.LogWarning("Integration event processing reported errors: {Error}", result.ErrorValue);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Integration event background processing failed: {ErrorMessage}", ex.Message);
            }
        }
    }
}
