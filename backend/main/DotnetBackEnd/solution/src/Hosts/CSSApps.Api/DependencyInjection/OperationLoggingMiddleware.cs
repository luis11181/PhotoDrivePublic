using System.Diagnostics;
using CSSApps.Shared.Utils.ApiHelpers.Middlewares;

namespace CSSApps.Api.DependencyInjection;

/// <summary>Writes one readable start/completion pair for every browser-facing backend request.</summary>
internal sealed class OperationLoggingMiddleware(RequestDelegate next, ILogger<OperationLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var source = SourceFor(context.Request.Path);
        var requestId = context.Request.Headers[CorrelationIdMiddleware.CorrelationHeaderKey].FirstOrDefault()
            ?? context.TraceIdentifier;
        var stopwatch = Stopwatch.StartNew();

        logger.LogInformation(
            "[{Source}] {Method} {Path} started requestId={RequestId}",
            source,
            context.Request.Method,
            context.Request.Path.Value,
            requestId);

        try
        {
            await next(context);
            stopwatch.Stop();
            logger.LogInformation(
                "[{Source}] {Method} {Path} completed status={StatusCode} elapsedMs={ElapsedMilliseconds} requestId={RequestId}",
                source,
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                requestId);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            stopwatch.Stop();
            logger.LogWarning(
                "[{Source}] {Method} {Path} cancelled elapsedMs={ElapsedMilliseconds} requestId={RequestId}",
                source,
                context.Request.Method,
                context.Request.Path.Value,
                stopwatch.ElapsedMilliseconds,
                requestId);
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            logger.LogError(
                exception,
                "[{Source}] {Method} {Path} failed elapsedMs={ElapsedMilliseconds} requestId={RequestId}",
                source,
                context.Request.Method,
                context.Request.Path.Value,
                stopwatch.ElapsedMilliseconds,
                requestId);
            throw;
        }
    }

    private static string SourceFor(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.Contains("/users", StringComparison.OrdinalIgnoreCase)) return "users.api";
        if (value.Contains("/developer", StringComparison.OrdinalIgnoreCase)) return "media.maintenance";
        if (value.Contains("/items", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/photos", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/uploads", StringComparison.OrdinalIgnoreCase)) return "media.api";
        return "system.api";
    }
}
