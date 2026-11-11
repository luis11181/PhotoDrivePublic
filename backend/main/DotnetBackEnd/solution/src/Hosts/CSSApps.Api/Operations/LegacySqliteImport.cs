// REMOVE-WITH-SQLITE-IMPORT: part of the one-time v1 (SQLite) to v2 (Turso) import. Development was imported on 2026-11-09 and Production on 2026-11-10; delete it once the v1 files are no longer kept (search the repo for this marker).
using System.Globalization;
using CSSApps.Shared.Infrastructure.Data.Turso;
using TursoConnection = Turso.Data.Sqlite.SqliteConnection;

namespace CSSApps.Api.Operations;

/// <summary>
/// One-time operator import of the previous two-file SQLite installation (catalog + Users) into the shared Turso
/// database. The source files are opened read-only and never modified. The target must be freshly migrated and
/// contain no libraries or user accounts, so an existing installation can never be overwritten. Derived search
/// tables and EF history are not copied: the target already has its own history, and search is rebuilt on start.
/// </summary>
internal sealed class LegacySqliteImport(TursoDatabase database, ILogger<LegacySqliteImport> logger)
{
    private const int RowsPerStatement = 50;

    private static readonly HashSet<string> SkippedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "__EFMigrationsHistory", "__EFMigrationsLock", "derived_schema_versions", "sqlite_sequence",
        "catalog_search", "timeline_event_search", "hosts", "cluster_settings"
    };

    public async Task<IReadOnlyDictionary<string, long>> ImportAsync(string catalogPath, string usersPath, CancellationToken cancellationToken)
    {
        foreach (var path in new[] { catalogPath, usersPath })
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Legacy SQLite database was not found.", path);
        }

        await using var target = database.OpenConnection(DatabaseRouteKind.Cloud);
        var copied = new Dictionary<string, long>(StringComparer.Ordinal);
        // Rows are copied table by table, so parents and children arrive in arbitrary order; check references at the end.
        await ExecuteAsync(target, "PRAGMA foreign_keys = OFF;", cancellationToken);
        try
        {
            // Keep the empty-target check, all copies, and verification in one transaction. A failed import
            // rolls back completely, including migration seed rows replaced by the copy, and can be retried.
            await using var transaction = await target.BeginTransactionAsync(cancellationToken);
            if (await CountAsync(target, "libraries", cancellationToken) != 0 || await CountAsync(target, "users", cancellationToken) != 0)
                throw new InvalidOperationException("The target database already contains libraries or accounts. Import only into a new, freshly migrated database.");

            foreach (var sourcePath in new[] { usersPath, catalogPath })
            {
                await using var source = new TursoConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
                await source.OpenAsync(cancellationToken);
                foreach (var table in await TablesAsync(source, cancellationToken))
                {
                    if (SkippedTables.Contains(table) || table.StartsWith("catalog_search_", StringComparison.Ordinal) || table.StartsWith("timeline_event_search_", StringComparison.Ordinal)) continue;
                    copied[table] = await CopyTableAsync(source, target, table, cancellationToken);
                    logger.LogInformation("[database.import] copied table={Table} rows={Rows}", table, copied[table]);
                }
            }
            await DatabaseForeignKeyCheck.VerifyAsync(target, cancellationToken);

            foreach (var (table, rows) in copied)
            {
                var targetRows = await CountAsync(target, table, cancellationToken);
                if (targetRows != rows) throw new InvalidOperationException($"Imported row count mismatch for '{table}': {rows} read, {targetRows} stored.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await ExecuteAsync(target, "PRAGMA foreign_keys = ON;", CancellationToken.None);
        }

        return copied;
    }

    private static async Task<long> CopyTableAsync(TursoConnection source, TursoConnection target, string table, CancellationToken cancellationToken)
    {
        var targetColumns = await ColumnsAsync(target, table, cancellationToken);
        if (targetColumns.Count == 0) throw new InvalidOperationException($"Table '{table}' does not exist in the target database. Apply migrations first.");
        var sourceColumns = await ColumnsAsync(source, table, cancellationToken);
        var missing = sourceColumns.Except(targetColumns, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Target table '{table}' lacks source column(s) {string.Join(", ", missing)}; the source schema is newer than this build.");

        var columnList = string.Join(", ", sourceColumns.Select(Quote));
        await ExecuteAsync(target, $"DELETE FROM {Quote(table)};", cancellationToken);

        await using var read = source.CreateCommand();
        read.CommandText = $"SELECT {columnList} FROM {Quote(table)};";
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        var batch = new List<object?[]>(RowsPerStatement);
        long rows = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object?[sourceColumns.Count];
            for (var index = 0; index < values.Length; index++) values[index] = reader.IsDBNull(index) ? DBNull.Value : reader.GetValue(index);
            batch.Add(values);
            if (batch.Count == RowsPerStatement)
            {
                rows += await InsertAsync(target, table, columnList, sourceColumns.Count, batch, cancellationToken);
                batch.Clear();
            }
        }
        if (batch.Count > 0) rows += await InsertAsync(target, table, columnList, sourceColumns.Count, batch, cancellationToken);
        return rows;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Identifiers come from the database schema and are quoted; values are bound.")]
    private static async Task<int> InsertAsync(TursoConnection target, string table, string columnList, int columnCount, List<object?[]> batch, CancellationToken cancellationToken)
    {
        await using var insert = target.CreateCommand();
        var rows = new List<string>(batch.Count);
        for (var row = 0; row < batch.Count; row++)
        {
            var placeholders = new string[columnCount];
            for (var column = 0; column < columnCount; column++)
            {
                var name = $"$p{row}_{column}";
                placeholders[column] = name;
                insert.Parameters.AddWithValue(name, batch[row][column]);
            }
            rows.Add("(" + string.Join(", ", placeholders) + ")");
        }
        insert.CommandText = $"INSERT INTO {Quote(table)} ({columnList}) VALUES {string.Join(", ", rows)};";
        return await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> TablesAsync(TursoConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND sql NOT LIKE 'CREATE VIRTUAL TABLE%' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        return tables;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The table name comes from the database schema and is quoted.")]
    private static async Task<List<string>> ColumnsAsync(TursoConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info({Literal(table)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(0));
        return columns;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The table name comes from a fixed list or the schema and is quoted.")]
    private static async Task<long> CountAsync(TursoConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Quote(table)};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only fixed statements are executed.")]
    private static async Task ExecuteAsync(TursoConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
