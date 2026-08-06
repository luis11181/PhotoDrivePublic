using CSSApps.Shared.Infrastructure;
using CSSApps.Shared.Infrastructure.IntegrationEvents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;
using Serilog;
using Serilog.Events;

namespace CSSApps.Shared.DependencyInjection;

public static class DependencyInjection
{
    /// <summary>
    ///     Adds some dependencies like TimeProvider, HttpClient, etc
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <param name="enviroment"></param>
    /// <returns></returns>
    public static IServiceCollection AddSharedModuleUtilities(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment enviroment)
    {

        services.AddHttpContextAccessor();

        // Add Feature Management
        services.AddFeatureManagement();

        services.AddSingleton(TimeProvider.System);


        #region WebApp services

        //! configure DI for application services
        //services.AddScoped<IAuthUtils, AuthUtils>();
        //services.AddScoped<IAuthService, AuthService>();

        services.AddHttpClient();

        #endregion

        return services;
    }


    /// <summary>
    ///     Adds some dependencies like TimeProvider, HttpClient, etc
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseSharedModuleUtilities(this IApplicationBuilder app)
    {
        return app;
    }

    /// <summary>
    ///     Adds some shared infrastructure dependencies from aws, like S3, DynamoDB, etc
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <param name="enviroment"></param>
    /// <returns></returns>
    public static IServiceCollection AddSharedModuleAwsInfrastructure(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment enviroment)
    {
        #region Infrastructure services

        //! Infrastructure services

        services.AddAWSInfrastructure(configuration);

        #endregion

        return services;
    }


    /// <summary>
    ///     Adds some shared infrastructure dependencies for integration events
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <param name="enviroment"></param>
    /// <returns></returns>
    public static IServiceCollection AddSharedModuleIntegrationEvents(
        this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment enviroment)
    {
        #region Infrastructure services

        //! Infrastructure services

        services.AddIntegrationEvents(configuration);

        #endregion


        return services;
    }

    /// <summary>
    ///     Adds the shared configuration for the logger, using Serilog
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <param name="host"></param>
    /// <returns></returns>
    public static IServiceCollection AddLogger(this IServiceCollection services, IConfiguration configuration,
        ConfigureHostBuilder host)
    {
        services.AddHttpContextAccessor(); // Ensure IHttpContextAccessor is registered

        // Determine log path: use shared folder in production, local folder otherwise
        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Development";
        var logsBasePath = environment == "Production" && Directory.Exists("/opt/cssapps/shared/logs")
            ? "/opt/cssapps/shared/logs"
            : "Logs";

        //    configuration to use aws cloudwatch sink

        //            // Set up CloudWatch log options
        //var cloudWatchOptions = new Serilog.Sinks.AwsCloudWatch.CloudWatchSinkOptions
        //{
        //    LogGroupName = "your-log-group", // Your CloudWatch Log Group
        //    LogStreamNameProvider = new DefaultLogStreamProvider(),
        //    TextFormatter = new Serilog.Formatting.Compact.CompactJsonFormatter(), // Use JSON format for structured logging
        //    MinimumLogEventLevel = LogEventLevel.Information, // Set minimum log level
        //    CreateLogGroup = true, // Automatically create the log group if it doesn't exist
        //    BatchSizeLimit = 100, // Number of events to send at once
        //    Period = TimeSpan.FromSeconds(5), // How often to send logs
        //    RetryAttempts = 5, // Retry attempts on failure
        //};


        //            // Add CloudWatch as a logging sink
        //    .WriteTo.AmazonCloudWatch(cloudWatchOptions)

        //! logger
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.With(new HttpContextEnricher(services.BuildServiceProvider()
                .GetRequiredService<IHttpContextAccessor>()))
            .WriteTo.Logger(l => l.Filter.ByIncludingOnly(e => e.Level == LogEventLevel.Error)
                .WriteTo.File(Path.Combine(logsBasePath, "Error-.log"), rollingInterval: RollingInterval.Month,
                    fileSizeLimitBytes: 10000000, rollOnFileSizeLimit: true))
            .WriteTo.Logger(l => l.Filter.ByIncludingOnly(e => e.Level == LogEventLevel.Fatal)
                .WriteTo.File(Path.Combine(logsBasePath, "Fatal-.log"), rollingInterval: RollingInterval.Month,
                    fileSizeLimitBytes: 10000000, rollOnFileSizeLimit: true))

#if DEBUG
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Verbose)
#endif
            .CreateLogger(); //Serilog's global, statically accessible logger, is set via Log.Logger and can be invoked using the static methods on the Log class.

        //*logging, clear the default providers and add the serilog provider
        host.UseSerilog(); // use serilog with the config above


        return services;
    }
}
