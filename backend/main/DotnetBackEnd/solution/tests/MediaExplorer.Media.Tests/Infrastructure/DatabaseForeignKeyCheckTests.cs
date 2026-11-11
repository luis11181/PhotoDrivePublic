using CSSApps.Shared.Infrastructure.Data.Turso;
using TursoConnection = Turso.Data.Sqlite.SqliteConnection;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class DatabaseForeignKeyCheckTests
{
    [Theory]
    [InlineData("INSERT INTO children VALUES (1, 1, 10, 20);", null)]
    [InlineData("INSERT INTO children VALUES (1, 1, 10, NULL);", null)]
    [InlineData("INSERT INTO children VALUES (1, 999, NULL, NULL);", "Database foreign-key check failed in table 'children'.")]
    [InlineData("INSERT INTO children VALUES (1, 1, 10, 99);", "Database foreign-key check failed in table 'children'.")]
    [InlineData("CREATE TABLE implicit_children (Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES parents);",
        "Cannot verify an implicit foreign key in table 'implicit_children'.")]
    public async Task Verifies_single_and_composite_references(string sql, string? expectedError)
    {
        var path = Path.Combine(Path.GetTempPath(), $"photodrive-fk-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var connection = new TursoConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    PRAGMA foreign_keys = OFF;
                    CREATE TABLE parents (Id INTEGER PRIMARY KEY, A INTEGER, B INTEGER, UNIQUE (A, B));
                    INSERT INTO parents VALUES (1, 10, 20);
                    CREATE TABLE children (Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES parents(Id), X INTEGER, Y INTEGER,
                        FOREIGN KEY (X, Y) REFERENCES parents(A, B));
                    {sql}
                    """;
                await command.ExecuteNonQueryAsync();
            }

            if (expectedError is null)
            {
                await DatabaseForeignKeyCheck.VerifyAsync(connection, CancellationToken.None);
                return;
            }
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DatabaseForeignKeyCheck.VerifyAsync(connection, CancellationToken.None));
            Assert.Equal(expectedError, error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
