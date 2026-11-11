using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Infrastructure.Data.Turso;

namespace CSSApps.Api.DependencyInjection;

/// <summary>
/// Chooses the database connection for each request before any DbContext is created: read-only methods use the
/// local replica; everything else commits directly to Turso Cloud. When a write's response starts, a replica pull
/// begins in the background; read-only requests wait for such a pull first, so this host always reads its own writes.
/// </summary>
internal sealed class DatabaseRouteMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, DatabaseRoute route, TursoDatabase database, CloudConnectionLease lease)
    {
        var method = context.Request.Method;
        var readOnly = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        if (readOnly && context.GetEndpoint()?.Metadata.GetMetadata<UseCloudDatabaseAttribute>() is null)
        {
            route.UseReplica();
            await database.WaitForPendingPullAsync();
        }
        else if (database.IsCloud)
        {
            context.Response.OnStarting(() =>
            {
                database.SchedulePullAfterWrite();
                return Task.CompletedTask;
            });
        }

        try
        {
            await next(context);
        }
        catch
        {
            // A failed request may leave a remote session mid-transaction; never lend it to another request.
            lease.DiscardOnRelease();
            throw;
        }
    }
}
