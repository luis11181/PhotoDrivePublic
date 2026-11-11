using System.Data.Common;
using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class StorageUsageService(MediaCatalogDbContext db, LibraryBootstrapper bootstrapper, IMemoryCache cache) : IStorageUsageService
{
    // All joins are scoped before aggregation. A shared original is counted once per library.
    private const string Scope = """
        WITH RECURSIVE refs AS (
          SELECT f.Id FileId, f.Name, f.ParentFolderId, f.TrashedAt, v.Id VersionId,
            v.BlobSha256, v.SizeBytes, v.MimeType, v.CreatedAt,
            CASE WHEN v.Id = f.CurrentVersionId THEN 1 ELSE 0 END IsCurrent
          FROM files f JOIN file_versions v ON v.FileId = f.Id WHERE f.LibraryId = $library
        ), originals AS (
          SELECT b.Sha256, b.SizeBytes, MIN(r.CreatedAt) AddedAt,
            MAX(CASE WHEN r.TrashedAt IS NULL THEN 1 ELSE 0 END) HasActive,
            MAX(r.IsCurrent) HasCurrent,
            MAX(CASE WHEN r.MimeType LIKE 'video/%' THEN 1 ELSE 0 END) HasVideo
          FROM refs r JOIN blobs b ON b.Sha256 = r.BlobSha256 GROUP BY b.Sha256
        ), tree(RootId, Id) AS (
          SELECT Id, Id FROM folders WHERE LibraryId = $library
          UNION
          SELECT t.RootId, f.Id FROM tree t JOIN folders f ON f.ParentFolderId = t.Id WHERE f.LibraryId = $library
        )
        """;

    private async Task<DbCommand> CommandAsync(Guid libraryId, string sql, CancellationToken ct)
    {
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = Scope + sql;
        Add(command, "$library", libraryId.ToString().ToUpperInvariant());
        return command;
    }
    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
    }

    public async Task<Result<StorageUsageRes>> GetAsync(Guid userId, bool refresh, CancellationToken cancellationToken)
    {
        var library = await bootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var cacheKey = $"media.storage-usage:{library.Id:N}";
        if (!refresh && cache.TryGetValue(cacheKey, out StorageUsageRes? cached) && cached is not null)
            return cached;

        // One read transaction keeps totals and breakdowns consistent during concurrent uploads.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        long physical, logical, trash, retained, video;
        await using (var command = await CommandAsync(library.Id, """
            SELECT COALESCE(SUM(SizeBytes),0),
              (SELECT COALESCE(SUM(SizeBytes),0) FROM refs),
              COALESCE(SUM(CASE WHEN HasActive=0 THEN SizeBytes ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN HasCurrent=0 THEN SizeBytes ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN HasVideo=1 THEN SizeBytes ELSE 0 END),0) FROM originals
            """, cancellationToken))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            physical=reader.GetInt64(0); logical=reader.GetInt64(1); trash=reader.GetInt64(2); retained=reader.GetInt64(3); video=reader.GetInt64(4);
        }
        var growth = new List<StorageBucket>();
        await using (var command = await CommandAsync(library.Id, "SELECT substr(AddedAt,1,7), SUM(SizeBytes), COUNT(*) FROM originals GROUP BY substr(AddedAt,1,7) ORDER BY 1", cancellationToken))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken)) growth.Add(new(reader.GetString(0),reader.GetInt64(1),reader.GetInt64(2)));
        }
        var folders = new List<StorageFolder>();
        await using (var command = await CommandAsync(library.Id, """
            SELECT f.Id, f.Name, SUM(x.SizeBytes) Bytes FROM (
              SELECT DISTINCT t.RootId, o.Sha256, o.SizeBytes FROM tree t
              JOIN refs r ON r.ParentFolderId=t.Id JOIN originals o ON o.Sha256=r.BlobSha256
            ) x JOIN folders f ON f.Id=x.RootId GROUP BY f.Id ORDER BY Bytes DESC, f.Id LIMIT 20
            """, cancellationToken))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken)) folders.Add(new(Guid.Parse(reader.GetString(0)),reader.GetString(1),reader.GetInt64(2)));
        }
        await transaction.CommitAsync(cancellationToken);
        var usage = new StorageUsageRes(physical, logical, trash, retained, video, growth, folders, DateTime.UtcNow);
        cache.Set(cacheKey, usage, TimeSpan.FromSeconds(60));
        return usage;
    }

    public async Task<Result<StorageFilesRes>> FilesAsync(Guid userId, string category, string? month, Guid? folderId, int offset, CancellationToken cancellationToken)
    {
        if (offset < 0) return MediaErrors.Validation("Invalid page offset.");
        var predicate = category switch {
            "all" => "1=1",
            "trash" => "o.HasActive=0",
            "versions" => "o.HasCurrent=0",
            "video" => "o.HasVideo=1",
            "month" when DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", out _) => "substr(o.AddedAt,1,7)=$month",
            "folder" when folderId.HasValue => "r.ParentFolderId IN (SELECT Id FROM tree WHERE RootId=$folder)",
            _ => null
        };
        if (predicate is null) return MediaErrors.Validation("Invalid storage category.");
        var library = await bootstrapper.GetOrCreateAsync(userId, cancellationToken);
        await using var command = await CommandAsync(library.Id, $"""
            SELECT r.FileId, r.Name, r.SizeBytes, r.TrashedAt IS NOT NULL, r.VersionId, r.IsCurrent
            FROM refs r JOIN originals o ON o.Sha256=r.BlobSha256 WHERE {predicate}
            ORDER BY r.SizeBytes DESC, r.FileId, r.VersionId LIMIT 51 OFFSET $offset
            """, cancellationToken);
        Add(command,"$month",month ?? ""); Add(command,"$folder",folderId?.ToString().ToUpperInvariant() ?? ""); Add(command,"$offset",offset);
        var files = new List<StorageFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken)) files.Add(new(Guid.Parse(reader.GetString(0)),reader.GetString(1),reader.GetInt64(2),reader.GetInt64(3)!=0,Guid.Parse(reader.GetString(4)),reader.GetInt64(5)!=0));
        return new StorageFilesRes(files.Take(50).ToList(),files.Count>50);
    }
}
