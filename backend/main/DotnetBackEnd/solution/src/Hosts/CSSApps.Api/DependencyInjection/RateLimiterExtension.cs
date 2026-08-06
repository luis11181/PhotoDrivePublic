using System.Security.Claims;
using System.Threading.RateLimiting;
using CSSApps.Api.DependencyInjection.RateLimiting;
using CSSApps.Shared.Utils.ApiHelpers;
using CSSApps.Shared.Utils.Common.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CSSApps.Api.DependencyInjection;

internal static class RateLimiterExtension
{
    /// <summary>
    /// Policy name for stricter rate limiting on sensitive endpoints (login, password reset, etc.).
    /// </summary>
    public const string StrictPolicyName = "StrictPolicy";

    internal static IServiceCollection AddMyRateLimiter(this IServiceCollection services, IConfiguration configuration)
    {
        // Register and validate rate limiting options
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            // Use a service provider to access the validated options
            var serviceProvider = services.BuildServiceProvider();
            var rateLimitOptions = serviceProvider.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

            // Early exit if rate limiting is disabled
            if (!rateLimitOptions.Enabled)
            {
                return;
            }

            options.RejectionStatusCode = rateLimitOptions.RejectionStatusCode;

            // Add custom response for rate limit exceeded
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "application/problem+json";

                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
                    ? retryAfterValue.TotalSeconds
                    : rateLimitOptions.Authenticated.WindowSeconds;

                context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter).ToString();

                var problemDetails = new
                {
                    type = "https://httpstatuses.com/429",
                    title = "Too Many Requests",
                    status = rateLimitOptions.RejectionStatusCode,
                    detail = "Rate limit exceeded. Please try again later.",
                    retryAfterSeconds = (int)retryAfter
                };

                await context.HttpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
            };

            // // Main user-based rate limiter policy
            // options.AddPolicy(UserPolicyName, httpContext =>
            //     CreateUserBasedPartition(httpContext, rateLimitOptions));

            // Strict policy for sensitive endpoints (login, registration, password reset)
            options.AddPolicy(StrictPolicyName, httpContext =>
                CreateStrictPartition(httpContext, rateLimitOptions));

            // Set global limiter as default (applies to all endpoints without explicit policy)
            // Combines: 1) Non-partitioned global limit (safety net) + 2) User-based partition limit
            options.GlobalLimiter = CreateCombinedGlobalLimiter(rateLimitOptions);
        });

        return services;
    }

    /// <summary>
    /// Creates a combined global limiter that chains:
    /// 1. A user-based partitioned limit (per-user/IP limits) - PRIMARY, always active
    /// 2. A non-partitioned global limit (single bucket for all requests) - OPTIONAL safety net
    /// Both must permit the request for it to proceed.
    /// </summary>
    private static PartitionedRateLimiter<HttpContext> CreateCombinedGlobalLimiter(RateLimitingOptions options)
    {
        // 1. User-based partitioned limiter (per-user/IP limits) - PRIMARY, always active
        var userBasedLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            CreateUserBasedPartition(httpContext, options));

        // 2. Non-partitioned global limiter (safety net for entire system) - OPTIONAL
        if (!options.Global.Enabled)
        {
            return userBasedLimiter;
        }

        var globalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
            RateLimitPartition.GetFixedWindowLimiter("__global__", _ =>
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.Global.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.Global.WindowSeconds),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = options.Global.QueueLimit
                }));

        // Chain: user/IP-based first, then global safety net
        return PartitionedRateLimiter.CreateChained(userBasedLimiter, globalLimiter);
    }

    /// <summary>
    /// Creates a rate limit partition based on the authenticated user's ID.
    /// Works with JWT (Bearer token), Cookie auth, and Service API Key authentication.
    /// Falls back to IP-based limiting for anonymous users.
    /// </summary>
    private static RateLimitPartition<string> CreateUserBasedPartition(
        HttpContext httpContext,
        RateLimitingOptions options)
    {
        var (partitionKey, policyOptions, isServiceApiKey) = GetPartitionInfo(httpContext, options);

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = policyOptions.PermitLimit,
                Window = TimeSpan.FromSeconds(policyOptions.WindowSeconds),
                QueueProcessingOrder = MapQueueOrder(policyOptions.QueueProcessingOrder),
                QueueLimit = policyOptions.QueueLimit
            });
    }

    /// <summary>
    /// Creates a stricter rate limit partition for sensitive endpoints.
    /// Uses lower limits to prevent brute force attacks.
    /// </summary>
    private static RateLimitPartition<string> CreateStrictPartition(
        HttpContext httpContext,
        RateLimitingOptions options)
    {
        // For strict policy, use IP + endpoint as partition key to limit per-endpoint attacks
        var clientIp = RequestInfo.GetClientIpAddress(httpContext);
        var endpoint = httpContext.Request.Path.Value ?? "/";
        var partitionKey = $"strict:{clientIp}:{endpoint}";

        var strictOptions = options.StrictAuthentication;

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = strictOptions.PermitLimit,
                Window = TimeSpan.FromSeconds(strictOptions.WindowSeconds),
                QueueProcessingOrder = MapQueueOrder(strictOptions.QueueProcessingOrder),
                QueueLimit = strictOptions.QueueLimit
            });
    }

    /// <summary>
    /// Extracts partition information from the HTTP context.
    /// Returns the partition key, applicable policy options, and whether it's a service API key.
    /// </summary>
    private static (string PartitionKey, RateLimitPolicyOptions Options, bool IsServiceApiKey) GetPartitionInfo(
        HttpContext httpContext,
        RateLimitingOptions options)
    {
        var user = httpContext.User;

        // Check if user is authenticated
        if (user.Identity?.IsAuthenticated == true)
        {
            // Get user ID from claims (works for JWT, Cookie, and API Key auth)
            var userId = GetUserIdFromClaims(user);

            if (!string.IsNullOrEmpty(userId))
            {
                // // Check if this is a service API key authentication
                // var authMethod = user.FindFirstValue(ClaimTypes.AuthenticationMethod);
                // var isServiceApiKey = string.Equals(authMethod, "ServiceApiKey", StringComparison.OrdinalIgnoreCase);

                // if (isServiceApiKey)
                // {
                //     // Service API keys get higher limits
                //     return ($"service:{userId}", options.ServiceApiKey, true);
                // }

                // Regular authenticated user (JWT or Cookie)
                return ($"user:{userId}", options.Authenticated, false);
            }
        }

        // Anonymous user - fallback to IP-based partition using anonymous limits
        var clientIp = RequestInfo.GetClientIpAddress(httpContext);
        return ($"anon:{clientIp}", options.Anonymous, false);
    }

    /// <summary>
    /// Extracts the user ID from claims using multiple claim types for compatibility.
    /// Supports JWT (sub claim), Cookie auth (NameIdentifier), and custom claims.
    /// </summary>
    private static string? GetUserIdFromClaims(ClaimsPrincipal user)
    {
        // Use the shared utility that handles all authentication methods
        return AuthUtils.GetUserIdentifier(user);
    }

    /// <summary>
    /// Maps the configuration enum to the System.Threading.RateLimiting enum.
    /// </summary>
    private static QueueProcessingOrder MapQueueOrder(QueueProcessingOrderType order) =>
        order switch
        {
            QueueProcessingOrderType.NewestFirst => QueueProcessingOrder.NewestFirst,
            _ => QueueProcessingOrder.OldestFirst
        };

    internal static WebApplication UseMyRateLimiter(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

        if (!options.Enabled)
        {
            return app;
        }

        app.UseRateLimiter();

        return app;
    }
}
