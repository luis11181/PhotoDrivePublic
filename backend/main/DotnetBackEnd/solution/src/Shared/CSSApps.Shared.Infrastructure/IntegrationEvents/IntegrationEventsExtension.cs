using System.Reflection;
using System.Threading.Channels;
using CSSApps.Shared.Contracts.IntegrationEvents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scrutor;

namespace CSSApps.Shared.Infrastructure.IntegrationEvents;

/// <summary>
///     Extension methods for adding integration events to the service collection.
/// </summary>
public static class IntegrationEventsExtension
{
    /// <summary>
    ///     Adds in-process integration events to the service collection.
    /// </summary>
    public static IServiceCollection AddIntegrationEvents(this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(Channel.CreateUnbounded<IIntegrationEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        }));

        services.AddSingleton<IIntegrationEventPublisher, IntegrationEventBus>();

        services.AddHostedService<IntegrationEventBackgroundService>();

        services.Scan(scan => scan.FromApplicationDependencies(assembly => !IsTestAssembly(assembly))
            .AddClasses(classes => classes.AssignableTo(typeof(IIntegrationEventHandler<>)), publicOnly: false)
            .AsImplementedInterfaces()
            .WithScopedLifetime());

        return services;
    }

    private static bool IsTestAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? string.Empty;
        return name.Contains("IntegrationTests", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase);
    }

}
