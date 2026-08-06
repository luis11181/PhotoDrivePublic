using System.Collections.Concurrent;
using System.Threading.Channels;
using CSSApps.Shared.Contracts.IntegrationEvents;
using CSSApps.Shared.Contracts.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSSApps.Shared.Infrastructure.IntegrationEvents;

public sealed class IntegrationEventBus(
    IServiceProvider serviceProvider,
    Channel<IIntegrationEvent> channel,
    ILogger<IntegrationEventBus> logger)
    : IIntegrationEventPublisher
{
    private static readonly ConcurrentDictionary<Type, Type> HandlerServiceTypeDictionary = new();
    private static readonly ConcurrentDictionary<Type, Type> WrapperTypeDictionary = new();

    public async Task<Result> PublishAsync<TEvent>(
        TEvent integrationEvent,
        CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var eventType = integrationEvent.GetType();
        var handlerServiceType = HandlerServiceTypeDictionary.GetOrAdd(
            eventType,
            et => typeof(IIntegrationEventHandler<>).MakeGenericType(et));

        var handlerCount = 0;

        using (var scope = serviceProvider.CreateScope())
        {
            handlerCount = scope.ServiceProvider.GetServices(handlerServiceType)
                .Count();

            if (handlerCount == 0)
            {
                logger.LogInformation(
                    "Integration event {EventName} has no registered consumers. Event will be ignored.",
                    eventType.Name);
                return Result.Success();
            }
        }

        var tasks = Enumerable.Range(0, handlerCount)
            .Select(index => HandlerWrapper.Create(serviceProvider, handlerServiceType, eventType, index))
            .Select(wrapper => HandleWrapperAsync(wrapper, integrationEvent, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        return AggregateResults(results);
    }

    public async Task PublishQueuedAsync<TEvent>(
        TEvent integrationEvent,
        CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        await channel.Writer.WriteAsync(integrationEvent, cancellationToken);
    }

    private async Task<Result> HandleWrapperAsync(
        HandlerWrapper wrapper,
        IIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            return await wrapper.Handle(integrationEvent, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return CommonResultErrors.OperationCancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Integration event handler failed for {EventName}: {ErrorMessage}",
                integrationEvent.GetType().Name,
                ex.Message);
            return new ResultError($"Integration event handler failed: {ex.Message}", "INTEGRATION_EVENT_HANDLER_ERROR");
        }
    }

    private static Result AggregateResults(IEnumerable<Result> results)
    {
        var errors = results.Where(result => result.IsFailure)
            .Select(result => result.ErrorValue)
            .ToList();

        if (errors.Count == 0) return Result.Success();

        var aggregatedError = new ResultExtensions.CompositeResultError(errors.Select(e => e.Message));
        return Result.Failure(aggregatedError);
    }

    private abstract class HandlerWrapper
    {
        public abstract Task<Result> Handle(IIntegrationEvent integrationEvent, CancellationToken cancellationToken);

        public static HandlerWrapper Create(
            IServiceProvider serviceProvider,
            Type handlerServiceType,
            Type eventType,
            int handlerIndex)
        {
            var wrapperType = WrapperTypeDictionary.GetOrAdd(
                eventType,
                et => typeof(HandlerWrapper<>).MakeGenericType(et));

            return (HandlerWrapper)Activator.CreateInstance(
                wrapperType,
                serviceProvider,
                handlerServiceType,
                handlerIndex)!;
        }
    }

    private sealed class HandlerWrapper<TEvent>(
        IServiceProvider serviceProvider,
        Type handlerServiceType,
        int handlerIndex) : HandlerWrapper
        where TEvent : IIntegrationEvent
    {
        public override async Task<Result> Handle(
            IIntegrationEvent integrationEvent,
            CancellationToken cancellationToken)
        {
            using var scope = serviceProvider.CreateScope();

            var handler = scope.ServiceProvider.GetServices(handlerServiceType)
                .OfType<IIntegrationEventHandler<TEvent>>()
                .ElementAt(handlerIndex);

            return await handler.HandleAsync((TEvent)integrationEvent, cancellationToken);
        }
    }
}
