using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Infrastructure.Data.Turso;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Turso;
using Turso.Raw.Public;

namespace CSSApps.Api.DependencyInjection;

/// <summary>
/// Turns a failed Turso Cloud round trip into an explicit 503 so the browser can tell the user that nothing was
/// saved, instead of a generic server error. The failure is logged as an error and recorded for owner diagnostics.
/// </summary>
internal sealed class DatabaseUnavailableExceptionHandler(
    TursoDatabase database,
    ILogger<DatabaseUnavailableExceptionHandler> logger) : IExceptionHandler
{
    public const string ErrorCode = "database_unavailable";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!IsCloudConnectivityFailure(exception, httpContext.RequestAborted)) return false;

        database.ReportCloudFailure(exception);
        logger.LogError(exception, "[database] cloud database unreachable method={Method} path={Path}", httpContext.Request.Method, httpContext.Request.Path);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Database unavailable",
            Detail = "The shared PhotoDrive database could not be reached, so nothing was saved. Check the internet connection and try again.",
            Type = ErrorCode
        };
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await httpContext.Response.WriteAsJsonAsync(ApiRes.Failure(problem), cancellationToken);
        return true;
    }

    internal static bool IsCloudConnectivityFailure(Exception exception, CancellationToken requestAborted)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException:
                case TursoSyncException:
                case TaskCanceledException when !requestAborted.IsCancellationRequested:
                    return true;
                case TursoRemoteSqlException:
                    return false;
                case TursoException when current.Message.StartsWith("Remote request", StringComparison.Ordinal):
                    return true;
            }
        }

        return false;
    }
}
