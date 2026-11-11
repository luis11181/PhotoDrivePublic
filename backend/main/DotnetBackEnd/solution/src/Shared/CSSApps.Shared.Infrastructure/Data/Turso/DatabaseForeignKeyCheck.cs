using System.Data.Common;

namespace CSSApps.Shared.Infrastructure.Data.Turso;

/// <summary>Checks references explicitly because Turso does not implement PRAGMA foreign_key_check.</summary>
public static class DatabaseForeignKeyCheck
{
    public static async Task VerifyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        }

        foreach (var table in tables)
        {
            var columns = new List<(long Id, string Parent, string From, string To)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA foreign_key_list({Quote(table)});";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    // EF migrations always name the referenced columns. Fail closed for an unexpected schema.
                    // SQLite reports an implicit (primary-key) target as NULL; Turso reports an empty string.
                    if (reader.IsDBNull(4) || reader.GetString(4).Length == 0) throw new InvalidOperationException($"Cannot verify an implicit foreign key in table '{table}'.");
                    columns.Add((reader.GetInt64(0), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
                }
            }

            foreach (var key in columns.GroupBy(column => column.Id))
            {
                // Composite keys are exempt when any child column is null, matching SQLite's MATCH SIMPLE.
                var nonNull = string.Join(" AND ", key.Select(column => $"child.{Quote(column.From)} IS NOT NULL"));
                var matches = string.Join(" AND ", key.Select(column => $"parent.{Quote(column.To)} = child.{Quote(column.From)}"));
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT 1 FROM {Quote(table)} child
                    WHERE {nonNull} AND NOT EXISTS (
                        SELECT 1 FROM {Quote(key.First().Parent)} parent WHERE {matches}
                    ) LIMIT 1;
                    """;
                if (await command.ExecuteScalarAsync(cancellationToken) is not null)
                    throw new InvalidOperationException($"Database foreign-key check failed in table '{table}'.");
            }
        }
    }

    // All identifiers come from schema metadata, never from request input.
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
