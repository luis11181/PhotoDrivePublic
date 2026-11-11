// REMOVE-WITH-SQLITE-IMPORT: part of the one-time v1 (SQLite) to v2 (Turso) import. Development was imported on 2026-11-09 and Production on 2026-11-10; delete it once the v1 files are no longer kept (search the repo for this marker).
using CSSApps.Api.Operations;
using CSSApps.Shared.Infrastructure.Data.Turso;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TursoConnection = Turso.Data.Sqlite.SqliteConnection;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class LegacySqliteImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_copy_or_verification_rolls_back_all_tables_and_allows_retry(bool invalidReference)
    {
        var root = Path.Combine(Path.GetTempPath(), $"photodrive-import-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "target.db");
            var users = Path.Combine(root, "users.db");
            var catalog = Path.Combine(root, "catalog.db");
            await ExecuteAsync(target, """
                CREATE TABLE users (Id INTEGER PRIMARY KEY);
                CREATE TABLE roles (Id INTEGER PRIMARY KEY);
                INSERT INTO roles VALUES (99);
                CREATE TABLE libraries (Id INTEGER PRIMARY KEY, OwnerUserId INTEGER REFERENCES users(Id));
                """);
            await ExecuteAsync(users, """
                CREATE TABLE users (Id INTEGER PRIMARY KEY);
                INSERT INTO users VALUES (1);
                CREATE TABLE roles (Id INTEGER PRIMARY KEY);
                INSERT INTO roles VALUES (1);
                """);
            await ExecuteAsync(catalog, """
                CREATE TABLE libraries (Id INTEGER PRIMARY KEY, OwnerUserId INTEGER);
                INSERT INTO libraries VALUES (1, 1);
                """);
            await ExecuteAsync(catalog, invalidReference
                ? "UPDATE libraries SET OwnerUserId = 999;"
                : "CREATE TABLE z_missing_in_target (Id INTEGER PRIMARY KEY);");

            await using var database = new TursoDatabase(
                Options.Create(new TursoDatabaseOptions { Url = target }), NullLogger<TursoDatabase>.Instance);
            var import = new LegacySqliteImport(database, NullLogger<LegacySqliteImport>.Instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() => import.ImportAsync(catalog, users, CancellationToken.None));

            Assert.Equal(0L, await ScalarAsync(target, "SELECT COUNT(*) FROM users;"));
            Assert.Equal(0L, await ScalarAsync(target, "SELECT COUNT(*) FROM libraries;"));
            Assert.Equal(99L, await ScalarAsync(target, "SELECT Id FROM roles;"));
            Assert.Equal(1L, await ScalarAsync(users, "SELECT COUNT(*) FROM users;"));

            await ExecuteAsync(catalog, invalidReference
                ? "UPDATE libraries SET OwnerUserId = 1;"
                : "DROP TABLE z_missing_in_target;");
            var copied = await import.ImportAsync(catalog, users, CancellationToken.None);
            Assert.Equal(1L, copied["users"]);
            Assert.Equal(1L, copied["libraries"]);
            Assert.Equal(1L, await ScalarAsync(target, "SELECT Id FROM roles;"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => import.ImportAsync(catalog, users, CancellationToken.None));
            Assert.Equal(1L, await ScalarAsync(target, "SELECT COUNT(*) FROM libraries;"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new TursoConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string path, string sql)
    {
        await using var connection = new TursoConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
