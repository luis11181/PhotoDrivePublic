using Turso.Raw.Public;
using TursoConnection = Turso.Data.Sqlite.SqliteConnection;

namespace CSSApps.Shared.Infrastructure.Data.Turso;

/// <summary>
/// Direct Turso Cloud connection whose close is best effort. Turso expires idle remote sessions after a few seconds;
/// the driver then throws while closing (<c>stream not found</c>) even though it has already reset its state. A
/// request that idled (for example during an S3 upload) must not fail just because its connection is being
/// disposed, and the server discards abandoned sessions on its own.
/// </summary>
internal sealed class CloudSqliteConnection(string connectionString) : TursoConnection(connectionString)
{
    public override void Close()
    {
        try
        {
            base.Close();
        }
        catch (Exception exception) when (IsRemoteCloseFailure(exception))
        {
            // The local state is already closed; only the remote session cleanup failed.
        }
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        catch (Exception exception) when (IsRemoteCloseFailure(exception))
        {
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        catch (Exception exception) when (IsRemoteCloseFailure(exception))
        {
        }
    }

    private static bool IsRemoteCloseFailure(Exception exception) =>
        exception is TursoException or HttpRequestException or TaskCanceledException;
}
