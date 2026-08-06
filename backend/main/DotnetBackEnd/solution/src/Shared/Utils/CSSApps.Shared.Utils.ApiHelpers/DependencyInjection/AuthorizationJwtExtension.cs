using System.Text;
using CSSApps.Shared.Contracts.Constants;
using CSSApps.Users.Contracts.Constants;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace CSSApps.Shared.Utils.ApiHelpers.DependencyInjection;

public static class AuthorizationJwtExtension
{
    public static IServiceCollection AddMyJwtAuthentication(this IServiceCollection services,
        IConfiguration configuration)
    {
        //! add authentication and authorization
        var key = configuration.GetValue<string>("JwtSettings:AccessTokenSecret")!;

        ArgumentException.ThrowIfNullOrEmpty(key, nameof(key));
        // throw exception if the key is null or empty


        //this authorization wil automatically allow all requests to the controllers, we can add the [Authorize] attribute to the controllers or actions to restrict access,
        //or we can add the [Authorize(Roles = "admin")]
        services.AddAuthentication(x =>
            {
                x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(x =>
            {
                x.RequireHttpsMetadata = false;
                x.SaveToken = true;
                //x.Authority = "https://localhost:7003/";
                x.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = configuration["JwtSettings:ValidateIssuer"] == "true",
                    ValidateAudience = configuration["JwtSettings:ValidateAudience"] == "true",
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration["JwtSettings:Issuer"],
                    ValidAudience = configuration["JwtSettings:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
                };
                x.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        if (context.Exception.GetType() == typeof(SecurityTokenExpiredException))
                            context.Response.Headers.Append(CommonConstants.Headers.IsAuthTokenExpired, "true");
                        return Task.CompletedTask;
                    }
                };
#if DEBUG
                x.IncludeErrorDetails = true;
#endif
            });


        // this part is only necessary for config like policies, program work without it
        services.AddAuthorizationBuilder()
            .AddPolicy("Admin", policy => policy.RequireRole(Roles.Admin, Roles.SuperAdmin))
            .AddPolicy("User", policy => policy.RequireRole(Roles.FreeUser, Roles.PaidUser, Roles.Admin, Roles.SuperAdmin));
        //.AddPolicy("money_amendments_permission", policy =>
        //  policy.RequireAssertion(context =>
        //      context.User.HasClaim(c => c.Type == "Permission" && c.Value == "money_amendments")));

        return services;
    }

    public static WebApplication UseMyJwtAuthentication(this WebApplication app)
    {
        // debe ir antes de add authentication,
        app.UseAuthentication();

        app.UseAuthorization();

        return app;
    }
}
