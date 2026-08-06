using System.Reflection;
using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Application.Services;
using CSSApps.Users.Application.Services.Auth;
using CSSApps.Users.Application.Services.Auth.OAuth.Google;
using CSSApps.Users.Application.Services.Auth.Password;
using CSSApps.Users.Application.Services.Auth.Session;
using CSSApps.Users.Application.Settings;
using CSSApps.Users.Contracts.Interfaces;
using CSSApps.Users.Contracts.Interfaces.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace CSSApps.Users.Application;

public static class Extensions
{
    public static IServiceCollection AddApplicationLayerServices(this IServiceCollection services)
    {
        services.AddTransient<ILocalAuthenticationServices, AuthServices>();
        services.AddScoped<IUserAdministrationService, UserAdministrationService>();

        // Add authentication services
        services.AddScoped<Services.Auth.JwtAuth.JwtAuth>();
        services.AddScoped<PasswordService>();

        // Add OAuth services
        services.AddScoped<GoogleOAuthService, GoogleOAuthService>();
        services.AddHttpClient<GoogleOAuthService>();

        // Add new session and device services
        services.AddScoped<SessionService>();
        services.AddScoped<IUserContactLookupService, UserContactLookupService>();


        services.AddMemoryCache();
        services.AddScoped<IUserPermissionsService, UserPermissionsService>();


        //* add validated configuration options

        #region Configuration options and checks

        services
            .AddOptions<GoogleOAuthConfig>()
            .BindConfiguration(GoogleOAuthConfig.SectionName)
            .ValidateOnStart();

        services
            .AddOptions<FamilyAuthenticationOptions>()
            .BindConfiguration(FamilyAuthenticationOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(options =>
                    string.IsNullOrWhiteSpace(options.BootstrapOwner.InitialPassword) ||
                    PasswordService.IsPasswordStrong(options.BootstrapOwner.InitialPassword).IsSuccess,
                "The bootstrap initial password does not meet the password policy.")
            .ValidateOnStart();

        services
            .AddOptions<JwtSettings>()
            .BindConfiguration("JwtSettings")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<UserPermissionsOptions>()
            .BindConfiguration("UserPermissions")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        //builder.Services
        //.AddOptions<KeysSettings>()
        //.BindConfiguration("Keys")
        //.ValidateDataAnnotations()
        //.ValidateOnStart();

        //builder.Services
        //.AddOptions<DatabaseSettings>()
        //.BindConfiguration("DatabaseSettings")
        //.ValidateDataAnnotations()
        //.ValidateOnStart();

        #endregion

        return services;
    }
}
