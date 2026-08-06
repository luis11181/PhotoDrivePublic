using System.Text;
using CSSApps.Api.Authentication.ApiKey;
using CSSApps.Shared.Contracts.Constants;
using CSSApps.Users.Contracts.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using CSSApps.Users.Infrastructure.Data;
using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace CSSApps.Api.Authentication;

public static class AuthenticationExtensions
{
    public const string UnifiedScheme = "UnifiedAuth";
    public const string ServiceApiKeyScheme = "ServiceApiKey";

    /// <summary>
    /// Minimum recommended key length for HMAC-SHA256 (256 bits = 32 bytes).
    /// </summary>
    private const int MinimumKeyLengthBytes = 32;

    public static IServiceCollection AddMyAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtSettings = configuration.GetSection("JwtSettings");
        var key = jwtSettings["AccessTokenSecret"];

        // Validate JWT secret key
        ValidateJwtSecretKey(key, "AccessTokenSecret");

        // Configure Service API Key Services with validation
        services.AddOptions<ServiceApiKeysConfig>()
            .Bind(configuration.GetSection("ServiceApiKeys"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<ConfigurationServiceApiKeyValidator>();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = UnifiedScheme;
                options.DefaultChallengeScheme = UnifiedScheme;
            })
            .AddPolicyScheme(UnifiedScheme, "Unified Authentication", options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    // Authentication Priority Order (evaluated top to bottom):
                    // 1. Service API Key - for service-to-service calls
                    // 2. Bearer header - explicit JWT auth (Swagger, Postman, mobile apps)
                    // 1. Service API Key Header - Highest priority for service-to-service communication
                    if (context.Request.Headers.ContainsKey(CommonConstants.Headers.ApiKeyHeaderKey))
                    {
                        return ServiceApiKeyScheme;
                    }

                    // 2. Bearer Token in Authorization Header - Standard OAuth2/OpenID Connect pattern
                    //    Examples: Swagger "Authorize" button, mobile apps, SPAs, Postman
                    var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                    if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return JwtBearerDefaults.AuthenticationScheme;
                    }

                    // JWT is the only user authentication mechanism. It accepts either an
                    // Authorization bearer header or the HttpOnly access_token cookie used by React.
                    return JwtBearerDefaults.AuthenticationScheme;
                };
            })
            .AddJwtBearer(options =>
            {
                // In production, always require HTTPS
                //options.RequireHttpsMetadata = !IsDevEnvironment(configuration);
                options.SaveToken = true;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = configuration.GetValue("JwtSettings:ValidateIssuer", true),
                    ValidateAudience = configuration.GetValue("JwtSettings:ValidateAudience", true),
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration["JwtSettings:Issuer"],
                    ValidAudience = configuration["JwtSettings:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key!)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    // Add clock skew tolerance (default is 5 minutes, reduce for tighter security)
                    ClockSkew = TimeSpan.FromMinutes(1),
                    // Require expiration claim
                    RequireExpirationTime = true
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                        if (string.IsNullOrEmpty(authHeader) &&
                            context.Request.Cookies.TryGetValue("access_token", out var token) &&
                            !string.IsNullOrEmpty(token))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        if (context.Exception is SecurityTokenExpiredException)
                        {
                            context.Response.Headers.Append(CommonConstants.Headers.IsAuthTokenExpired, "true");
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        // JWT signature validation is not enough for immediate revocation. Check the
                        // current user stamp and persisted session on every authenticated request.
                        var userIdValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                        var securityStampValue = context.Principal?.FindFirstValue("security_stamp");
                        var sessionIdValue = context.Principal?.FindFirstValue("session_id");
                        if (!Guid.TryParse(userIdValue, out var userId) ||
                            !Guid.TryParseExact(securityStampValue, "N", out var securityStamp) ||
                            !Guid.TryParse(sessionIdValue, out var sessionId))
                        {
                            context.Fail("Invalid session");
                            return;
                        }

                        var dbContext = context.HttpContext.RequestServices.GetRequiredService<UsersDbContext>();
                        var valid = await dbContext.Users.AsNoTracking().AnyAsync(user =>
                                user.Id == userId &&
                                !user.IsServiceAccount &&
                                user.AccountStatus != HumanAccountStatus.Disabled &&
                                user.SecurityStamp == securityStamp,
                            context.HttpContext.RequestAborted);
                        if (!valid)
                        {
                            context.Fail("Invalid session");
                            return;
                        }

                        var activeSession = await dbContext.UserSessions.AsNoTracking().AnyAsync(session =>
                                session.Id == sessionId && session.UserId == userId && session.IsActive,
                            context.HttpContext.RequestAborted);
                        if (!activeSession) context.Fail("Session was revoked");
                    }
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ServiceApiKeySchemeHandler>(ServiceApiKeyScheme, _ => { });

        // Authorization Policies
        services.AddAuthorizationBuilder()
            .AddPolicy("Owner", policy => policy.RequireRole(Roles.Owner))
            .AddPolicy("Admin", policy => policy.RequireRole(Roles.Owner))
            .AddPolicy("User", policy => policy.RequireRole(Roles.Member, Roles.Owner))
            .AddPolicy("Service", policy => policy.RequireRole("service"))
            .AddPolicy("ServiceApiKeyOnly", policy => policy
                .AddAuthenticationSchemes(ServiceApiKeyScheme)
                .RequireAuthenticatedUser());

        return services;
    }

    public static WebApplication UseMyAuthentication(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseMiddleware<PasswordChangeRequiredMiddleware>();
        app.UseAuthorization();
        return app;
    }

    private static void ValidateJwtSecretKey(string? key, string settingName)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException(
                $"JwtSettings:{settingName} is missing in configuration. " +
                "Please configure a secure secret key.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(key);
        if (keyBytes.Length < MinimumKeyLengthBytes)
        {
            throw new InvalidOperationException(
                $"JwtSettings:{settingName} must be at least {MinimumKeyLengthBytes} bytes " +
                $"(currently {keyBytes.Length} bytes). Use a longer, more secure key.");
        }

        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase) &&
            key.StartsWith("replace-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"JwtSettings:{settingName} still contains a placeholder and must be replaced outside Development.");
        }
    }

}
