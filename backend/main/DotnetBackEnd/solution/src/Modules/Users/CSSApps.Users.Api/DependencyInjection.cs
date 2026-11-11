using CSSApps.Users.Application;
using CSSApps.Users.Infrastructure;
using CSSApps.Users.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CSSApps.Users.Api;

public static class DependencyInjection
{
    public static IServiceCollection AddUsersModule(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment enviroment)
    {
        //! Application services

        services.AddApplicationLayerServices();

        services.AddInfrastructureLayerServices(configuration, enviroment);

        #region WebApp services

        //! 

        #endregion

        return services;
    }

    public static WebApplication UseUsersModule(this WebApplication app)
    {
        return app;
    }
}