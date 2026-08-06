using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

public sealed record OperatorDatabaseRestoreResult(
    string BackupSetId,
    long FileCount,
    long UserCount,
    string? RollbackDirectory);

/// <summary>Verifies and replaces the paired SQLite databases as one offline operator operation.</summary>
public sealed class OperatorDatabaseRestoreService(IConfiguration configuration)
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is an injected operator service with one cohesive verify/apply surface.")]
    public async Task<OperatorDatabaseRestoreResult> VerifyAsync(
        string catalogSnapshotPath,
        string usersSnapshotPath,
        CancellationToken cancellationToken)
    {
        var catalogPath = Path.GetFullPath(catalogSnapshotPath);
        var usersPath = Path.GetFullPath(usersSnapshotPath);
        var backupSetId = PairedBackupSetId(catalogPath, usersPath);
        var catalog = await CatalogBackupService.VerifyRestoreAsync(catalogPath, cancellationToken);
        var users = await UsersBackupService.VerifyRestoreAsync(usersPath, cancellationToken);
        await CatalogBackupPipeline.VerifyCatalogUsersCoverageAsync(catalogPath, usersPath, cancellationToken);
        return new OperatorDatabaseRestoreResult(backupSetId, catalog.FileCount, users.UserCount, RollbackDirectory: null);
    }

    public async Task<OperatorDatabaseRestoreResult> ApplyAsync(
        string catalogSnapshotPath,
        string usersSnapshotPath,
        CancellationToken cancellationToken)
    {
        var verification = await VerifyAsync(catalogSnapshotPath, usersSnapshotPath, cancellationToken);
        var catalogDestination = DatabasePath("MediaCatalog");
        var usersDestination = DatabasePath("UsersModule");
        if (string.Equals(catalogDestination, usersDestination, StringComparison.Ordinal))
            throw new InvalidOperationException("Catalog and Users connection strings must identify different SQLite files.");

        var catalogSource = Path.GetFullPath(catalogSnapshotPath);
        var usersSource = Path.GetFullPath(usersSnapshotPath);
        if (string.Equals(catalogSource, catalogDestination, StringComparison.Ordinal) ||
            string.Equals(usersSource, usersDestination, StringComparison.Ordinal))
            throw new InvalidOperationException("A restore snapshot cannot also be the configured live database file.");

        Directory.CreateDirectory(Path.GetDirectoryName(catalogDestination)!);
        Directory.CreateDirectory(Path.GetDirectoryName(usersDestination)!);
        var rollbackDirectory = Path.Combine(
            Path.GetFullPath(configuration["MediaStorage:LocalDisk:BackupDirectory"]!),
            "restore-rollbacks",
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(rollbackDirectory);

        var catalogStage = $"{catalogDestination}.{Guid.CreateVersion7():N}.restore";
        var usersStage = $"{usersDestination}.{Guid.CreateVersion7():N}.restore";
        File.Copy(catalogSource, catalogStage, overwrite: false);
        File.Copy(usersSource, usersStage, overwrite: false);

        var catalogExisted = File.Exists(catalogDestination);
        var usersExisted = File.Exists(usersDestination);
        var preserved = new List<(string Live, string Rollback)>();
        try
        {
            PreserveCurrentFiles(catalogDestination, rollbackDirectory, preserved);
            PreserveCurrentFiles(usersDestination, rollbackDirectory, preserved);
            File.Move(catalogStage, catalogDestination, overwrite: true);
            File.Move(usersStage, usersDestination, overwrite: true);
            RemoveSqliteSidecars(catalogDestination);
            RemoveSqliteSidecars(usersDestination);
            SqliteConnection.ClearAllPools();
        }
        catch
        {
            if (!catalogExisted && File.Exists(catalogDestination)) File.Delete(catalogDestination);
            if (!usersExisted && File.Exists(usersDestination)) File.Delete(usersDestination);
            foreach (var (live, rollback) in preserved)
            {
                if (File.Exists(rollback)) File.Copy(rollback, live, overwrite: true);
            }
            SqliteConnection.ClearAllPools();
            throw;
        }
        finally
        {
            if (File.Exists(catalogStage)) File.Delete(catalogStage);
            if (File.Exists(usersStage)) File.Delete(usersStage);
        }

        return verification with { RollbackDirectory = rollbackDirectory };
    }

    private string DatabasePath(string connectionName)
    {
        var connectionString = configuration.GetConnectionString(connectionName)
            ?? throw new InvalidOperationException($"Connection string '{connectionName}' was not configured.");
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"Connection string '{connectionName}' must use an absolute SQLite path for restore.");
        return Path.GetFullPath(path);
    }

    private static string PairedBackupSetId(string catalogPath, string usersPath)
    {
        var catalogSet = S3CatalogBackupStore.CatalogBackupTimestamp(catalogPath);
        var usersSet = S3CatalogBackupStore.UsersBackupTimestamp(usersPath);
        BackupSetTimestamp.Validate(catalogSet);
        BackupSetTimestamp.Validate(usersSet);
        if (!string.Equals(catalogSet, usersSet, StringComparison.Ordinal))
            throw new InvalidOperationException("Catalog and Users snapshots must belong to the same backup set.");
        return catalogSet;
    }

    private static void PreserveCurrentFiles(
        string databasePath,
        string rollbackDirectory,
        List<(string Live, string Rollback)> preserved)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (!File.Exists(path)) continue;
            var rollback = Path.Combine(rollbackDirectory, Path.GetFileName(path));
            File.Copy(path, rollback, overwrite: false);
            preserved.Add((path, rollback));
        }
    }

    private static void RemoveSqliteSidecars(string databasePath)
    {
        foreach (var path in new[] { $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
