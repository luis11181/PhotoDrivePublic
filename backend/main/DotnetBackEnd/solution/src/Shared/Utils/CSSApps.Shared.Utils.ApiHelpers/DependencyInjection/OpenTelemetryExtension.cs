using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CSSApps.Shared.Utils.ApiHelpers.DependencyInjection;

public static class OpenTelemetryExtension
{
    public static IServiceCollection AddMyOpenTelemetry(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        var tracingOtlpEndpoint = configuration["OTLP_ENDPOINT_URL"];

        var otel = services.AddOpenTelemetry();

        // Configure OpenTelemetry Resources with the application name
        otel.ConfigureResource(resource => resource.AddService(environment.ApplicationName));

        // Add Metrics for ASP.NET Core and our custom metrics and export to Prometheus
        otel.WithMetrics(metrics => metrics
                // Metrics provider from OpenTelemetry
                .AddAspNetCoreInstrumentation()
                //.AddMeter(greeterMeter.Name)
                // Metrics provides by ASP.NET Core in .NET 8
                .AddMeter("Microsoft.AspNetCore.Hosting")
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
            //.AddPrometheusExporter()  ///install OpenTelemetry.Exporter.Prometheus.AspNetCore
        );

        // Add Tracing for ASP.NET Core and our custom ActivitySource and export to Jaeger
        otel.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation();
            tracing.AddHttpClientInstrumentation();
            //  .AddEntityFrameworkCoreInstrumentation()
            //  .AddRedisInstrumentation()
            if (tracingOtlpEndpoint != null)
                tracing.AddOtlpExporter(otlpOptions => { otlpOptions.Endpoint = new Uri(tracingOtlpEndpoint); });
            // Console exporter disabled for cleaner logs in development
            // else if (environment.IsDevelopment()) tracing.AddConsoleExporter();
        });


        return services;
    }

    public static WebApplication UseMyOpenTelemetry(this WebApplication app)
    {
        return app;
    }
}

//access metrics from metrics endpoint
//curl -k https://localhost:7275/metrics
