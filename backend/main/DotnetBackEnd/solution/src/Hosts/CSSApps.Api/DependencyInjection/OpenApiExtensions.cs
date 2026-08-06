using Microsoft.OpenApi;

namespace CSSApps.Api.DependencyInjection;

internal static class OpenApiExtensions
{
    internal static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Media Explorer API",
                Version = "v1",
                Description = "Local, authenticated API for the Media Explorer family library."
            });

            foreach (var xmlPath in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.xml", SearchOption.TopDirectoryOnly)
                         .Where(path => !Path.GetFileName(path).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                                        && !Path.GetFileName(path).StartsWith("System.", StringComparison.OrdinalIgnoreCase)))
            {
                options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
            }
        });

        return services;
    }

    internal static WebApplication UseSwaggerDocumentation(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return app;

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.DocumentTitle = "Media Explorer API";
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Media Explorer API v1");
        });
        return app;
    }
}
