using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSSApps.Shared.Infrastructure.Data.Turso;

/// <summary>Thrown when code running on a read-only (replica) route tries to write.</summary>
public sealed class ReplicaWriteException(string message) : InvalidOperationException(message);

/// <summary>
/// Turso replicas accept local writes silently and keep them forever without sending them to the cloud.
/// Every command issued through EF on the replica route is therefore checked and rejected unless it only reads.
/// </summary>
internal sealed partial class ReplicaReadOnlyInterceptor : DbCommandInterceptor
{
    public static ReplicaReadOnlyInterceptor Instance { get; } = new();

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        EnsureReadOnly(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        EnsureReadOnly(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        EnsureReadOnly(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        EnsureReadOnly(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        EnsureReadOnly(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        EnsureReadOnly(command);
        return ValueTask.FromResult(result);
    }

    internal static bool IsReadOnly(string sql) =>
        ReadStatement().IsMatch(sql) && !WriteKeyword().IsMatch(sql);

    private static void EnsureReadOnly(DbCommand command)
    {
        if (IsReadOnly(command.CommandText)) return;
        throw new ReplicaWriteException(
            "A read-only request attempted to write to the local database replica. " +
            "Mark the endpoint with [UseCloudDatabase] or perform the write in a mutating request.");
    }

    [GeneratedRegex(@"^\s*(SELECT|WITH|EXPLAIN)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadStatement();

    // Rejects data-changing statements hidden behind WITH. String literals rarely contain these keywords in
    // PhotoDrive's read queries; a false positive fails loudly, which is the safe direction.
    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|REPLACE|CREATE|DROP|ALTER)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteKeyword();
}
