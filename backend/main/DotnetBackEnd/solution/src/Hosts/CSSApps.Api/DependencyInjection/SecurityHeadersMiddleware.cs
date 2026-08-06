namespace CSSApps.Api.DependencyInjection;

/// <summary>
/// Middleware to add security headers required for Google Identity Services and other integrations.
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Cross-Origin-Opener-Policy: Required for Google Sign-In popup to work correctly
        // When FedCM is disabled, set to same-origin-allow-popups
        // https://developers.google.com/identity/gsi/web/guides/get-google-api-clientid#cross_origin_opener_policy
        context.Response.Headers.Append("Cross-Origin-Opener-Policy", "same-origin-allow-popups");
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Append("Referrer-Policy", "no-referrer");
        context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");

        await _next(context);
    }
}

/// <summary>
/// Extension methods for security headers middleware.
/// </summary>
public static class SecurityHeadersMiddlewareExtensions
{
    /// <summary>
    /// Adds security headers middleware to the application pipeline.
    /// Must be added early in the pipeline, before authentication.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
