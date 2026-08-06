using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using CSSApps.Users.Infrastructure.Services.Auth;
using Microsoft.AspNetCore.Authentication;

namespace CSSApps.Users.Infrastructure;

public static class Extensions
{
   public static IServiceCollection AddInfrastructureLayerServices(this IServiceCollection services,
       IConfiguration configuration, IHostEnvironment environment)
   {
      #region EF Core SQLite database

      var dbConnectionString = configuration.GetConnectionString("UsersModule") ??
                               throw new InvalidOperationException("Db connection string not found");

      // Register ModuleA DbContext
      services.AddDbContext<UsersDbContext>(options =>
          options.UseSqlite(dbConnectionString));

      //! injecting the IAppDbContext interface in case we want to isolate the dbcontext from the application layer
      services.AddScoped<IUsersDbContext>(provider => provider.GetRequiredService<UsersDbContext>());

      services
          .AddOptions<DevelopmentUserSeedingOptions>()
          .BindConfiguration(DevelopmentUserSeedingOptions.SectionName);

      // database initializer, has functions to do migrations and seed data if necessary, can be injected ans called from tests
      //services.AddScoped<ApplicationDbContextInitializer_ChatBot>();

      #endregion

      services.AddServices();
      services.AddScoped<UsersDataSeeder>();

      // Register Claims Transformation, Automatically runs on every authenticated request Loads user's roles and permissions from database
      services.AddTransient<IClaimsTransformation, AddUserPermissionsClaimsTransformation>();

      //add typed HTTP clients

      // services.AddHttpClient<WhatsAppApiClient>()
      //     .AddStandardResilienceHandler();

      return services;
   }

   private static IServiceCollection AddServices(this IServiceCollection services)
   {
      return services;
   }
}
