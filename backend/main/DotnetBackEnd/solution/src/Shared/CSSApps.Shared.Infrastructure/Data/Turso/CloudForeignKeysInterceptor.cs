using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSSApps.Shared.Infrastructure.Data.Turso;

/// <summary>
/// Turso Cloud enforces foreign keys (and therefore ON DELETE CASCADE) only for a remote session that ran
/// <c>PRAGMA foreign_keys = ON</c>, and the server discards sessions that sit idle for a few seconds. Before the
/// first command or transaction that follows an idle gap (for example an S3 upload or ffmpeg run in the middle of
/// a request or background job), re-enable foreign keys. That also replaces an expired session: the driver reports
/// it as a plain HTTP 404 ("stream not found") only once and then opens a new session, so the PRAGMA is retried
/// once and the caller's own statement runs on the fresh session. Inside a transaction the session cannot have been
/// replaced without failing the transaction, so nothing is done there.
/// </summary>
internal sealed class CloudForeignKeysInterceptor : DbCommandInterceptor, IDbTransactionInterceptor
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(3);
    private readonly ConditionalWeakTable<DbConnection, StrongBox<long>> _lastUse = new();

    public static CloudForeignKeysInterceptor Instance { get; } = new();

    /// <summary>Marks a connection whose foreign keys were just enabled (for example when lent from the pool).</summary>
    public void Touch(DbConnection connection) =>
        Interlocked.Exchange(ref _lastUse.GetValue(connection, static _ => new StrongBox<long>()).Value, Stopwatch.GetTimestamp());

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Refresh(command.Connection, command.Transaction is not null);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await RefreshAsync(command.Connection, command.Transaction is not null, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Refresh(command.Connection, command.Transaction is not null);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await RefreshAsync(command.Connection, command.Transaction is not null, cancellationToken);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Refresh(command.Connection, command.Transaction is not null);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await RefreshAsync(command.Connection, command.Transaction is not null, cancellationToken);
        return result;
    }

    public InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        Refresh(connection, inTransaction: false);
        return result;
    }

    public async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        await RefreshAsync(connection, inTransaction: false, cancellationToken);
        return result;
    }

    private void Refresh(DbConnection? connection, bool inTransaction)
    {
        using var pragma = PragmaIfIdle(connection, inTransaction);
        if (pragma is null) return;
        try
        {
            pragma.ExecuteNonQuery();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The driver closes the connection after reporting the expired session; reopen it with a new session.
            if (connection!.State != ConnectionState.Open) connection.Open();
            pragma.ExecuteNonQuery();
        }
    }

    private async Task RefreshAsync(DbConnection? connection, bool inTransaction, CancellationToken cancellationToken)
    {
        await using var pragma = PragmaIfIdle(connection, inTransaction);
        if (pragma is null) return;
        try
        {
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The driver closes the connection after reporting the expired session; reopen it with a new session.
            if (connection!.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private DbCommand? PragmaIfIdle(DbConnection? connection, bool inTransaction)
    {
        if (connection is null || connection.State != ConnectionState.Open) return null;
        var now = Stopwatch.GetTimestamp();
        var lastUse = _lastUse.GetValue(connection, static _ => new StrongBox<long>(Stopwatch.GetTimestamp()));
        var idle = Stopwatch.GetElapsedTime(Interlocked.Exchange(ref lastUse.Value, now), now);
        if (idle < IdleThreshold || inTransaction) return null;
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        return pragma;
    }
}
