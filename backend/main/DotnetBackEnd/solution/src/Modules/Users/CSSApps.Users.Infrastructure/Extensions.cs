using CSSApps.Users.Application.Interfaces;
using CSSApps.Shared.Infrastructure.Data.Turso;
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
      #region EF Core database (shared Turso database)

      services.AddTursoDatabase(configuration);
      services.AddDbContext<UsersDbContext>((provider, options) => provider.GetRequiredService<TursoDatabase>()
          .Configure(options, provider.GetRequiredService<DatabaseRoute>().Kind, TursoDatabase.UsersHistoryTable, provider.GetRequiredService<CloudConnectionLease>()));

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

      // Adds role claims (Owner/Member) on every authenticated request; see AddUserRolesClaimsTransformation.
      services.AddTransient<IClaimsTransformation, AddUserRolesClaimsTransformation>();

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
