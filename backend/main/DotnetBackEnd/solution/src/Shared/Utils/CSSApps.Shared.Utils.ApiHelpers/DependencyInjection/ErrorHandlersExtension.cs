using CSSApps.Shared.Utils.ApiHelpers.ErrorHandlers.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CSSApps.Shared.Utils.ApiHelpers.DependencyInjection;

public static class ErrorHandlersExtension
{
    public static IServiceCollection AddMyErrorHandlers(this IServiceCollection services, IHostEnvironment env)
    {
        // customize problem details depending on the environment
        services.AddProblemDetails(setup => { setup.CustomizeProblemDetails = ctx => { }; });

        //handles not found exceptions,  will execute first and try to handle the exception. If the exception isn't handled will continue to the next handler
        services.AddExceptionHandler<NotFoundExceptionHandler>();

        services.AddExceptionHandler<BadRequestExceptionHandler>();

        //handles all exceptions
        services.AddExceptionHandler<GlobalExceptionHandler>();

        return services;
    }


    public static WebApplication UseMyErrorHandlers(this WebApplication app)
    {
        //Converts Unhandled exceptions to Problem Details response
        app.UseExceptionHandler();

        // Returns the Problem Details response for (empty) non-successful responses
        app.UseStatusCodePages();

        return app;
    }
}