using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using CSSApps.Shared.Infrastructure.Data.Turso;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MediaExplorer.Media.Infrastructure;

namespace MediaExplorer.Media.Tests.TestSupport;

internal sealed class MediaTestHarness : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly IConfiguration _configuration;
    private readonly MemoryCache _storageSummaryCache = new(new MemoryCacheOptions());

    public MediaTestHarness(IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), $"media-explorer-tests-{Guid.NewGuid():N}");
        StagingDirectory = Path.Combine(RootDirectory, "staging");
        _databasePath = Path.Combine(RootDirectory, "catalog.db");
        var values = new Dictionary<string, string?>
        {
            ["MediaStorage:Imports:SourceDirectory"] = RootDirectory,
            ["MediaStorage:LocalDisk:StagingDirectory"] = StagingDirectory,
            ["MediaStorage:LocalDisk:ThumbnailDirectory"] = Path.Combine(RootDirectory, "thumbnails"),
            ["MediaStorage:LocalDisk:OriginalCacheDirectory"] = Path.Combine(RootDirectory, "blobs", "sha256"),
            ["MediaStorage:LocalDisk:OriginalCacheMaxBytes"] = (32L * 1024 * 1024).ToString(),
            ["MediaStorage:LocalDisk:BackupDirectory"] = Path.Combine(RootDirectory, "backups"),
            ["Database:Url"] = _databasePath,
            ["MediaStorage:ImageToolPath"] = "convert",
            ["MediaStorage:ImageIdentifyToolPath"] = "identify",
            ["MediaStorage:VideoThumbnailToolPath"] = "ffmpeg"
        };
        if (configurationOverrides is not null)
        {
            foreach (var (key, value) in configurationOverrides) values[key] = value;
        }
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public string RootDirectory { get; }
    public string StagingDirectory { get; }
    public IConfiguration Configuration => _configuration;
    public TestObjectStoreFactory ObjectStoreFactory { get; } = new();
    public IMediaMetadataExtractor? MetadataExtractorOverride { get; set; }
    public MediaMetadataWorkSignal MetadataWorkSignal { get; } = new();
    public ImportWorkSignal ImportWorkSignal { get; } = new();
    public CacheBuildWorkSignal CacheBuildWorkSignal { get; } = new();

    /// <summary>
    /// Starts from a copy of one database migrated once per test run: migrating took most of every test's setup.
    /// </summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(RootDirectory);
        File.Copy(await MigratedTemplate.Value, _databasePath);
    }

    private static readonly Lazy<Task<string>> MigratedTemplate = new(CreateMigratedTemplateAsync);

    private static async Task<string> CreateMigratedTemplateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"media-explorer-tests-template-{Guid.NewGuid():N}.db");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
        };
        await using var database = new TursoDatabase(
            Microsoft.Extensions.Options.Options.Create(new TursoDatabaseOptions { Url = path }),
            NullLogger<TursoDatabase>.Instance);
        var options = new DbContextOptionsBuilder<MediaCatalogDbContext>();
        database.Configure(options, DatabaseRouteKind.Cloud, TursoDatabase.MediaHistoryTable);
        await using (var dbContext = new MediaCatalogDbContext(options.Options))
        {
            await new MediaCatalogInitializer(dbContext).InitializeAsync();
        }

        // Move everything into the main file so a plain file copy is a complete database.
        await using var connection = database.OpenConnection(DatabaseRouteKind.Cloud);
        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpoint.ExecuteNonQueryAsync();
        return path;
    }

    public MediaCatalogDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MediaCatalogDbContext>();
        Database.Configure(options, DatabaseRouteKind.Cloud, TursoDatabase.MediaHistoryTable);
        return new MediaCatalogDbContext(options.Options);
    }

    /// <summary>The same connection setup the host uses, pointed at this test's local Turso file.</summary>
    public TursoDatabase Database => _database ??= new TursoDatabase(
        Microsoft.Extensions.Options.Options.Create(new TursoDatabaseOptions { Url = _databasePath }),
        NullLogger<TursoDatabase>.Instance);

    private TursoDatabase? _database;

    public MediaTestClient CreateService(MediaCatalogDbContext dbContext) => new(CreateResultService(dbContext));

    public MediaCatalogService CreateResultService(MediaCatalogDbContext dbContext)
    {
        var cache = new OriginalCache(_configuration);
        var thumbnailCache = new MediaThumbnailCache(_configuration);
        var processor = new FileMetadataProcessor(
            dbContext,
            MetadataExtractorOverride ?? new ExternalMediaMetadataExtractor(_configuration),
            ObjectStoreFactory,
            cache,
            thumbnailCache,
            UploaderIdentityDirectory,
            _configuration,
            NullLogger<FileMetadataProcessor>.Instance);
        return new MediaCatalogService(dbContext, new LibraryBootstrapper(dbContext), new CatalogAuthorization(dbContext), ObjectStoreFactory, cache, thumbnailCache, processor, MetadataWorkSignal, UploaderIdentityDirectory, _configuration, _storageSummaryCache, NullLogger<MediaCatalogService>.Instance);
    }

    public FileMetadataProcessor CreateMetadataProcessor(MediaCatalogDbContext dbContext) =>
        new(
            dbContext,
            MetadataExtractorOverride ?? new ExternalMediaMetadataExtractor(_configuration),
            ObjectStoreFactory,
            new OriginalCache(_configuration),
            new MediaThumbnailCache(_configuration),
            UploaderIdentityDirectory,
            _configuration,
            NullLogger<FileMetadataProcessor>.Instance);

    public ImportService CreateImportService(MediaCatalogDbContext dbContext)
    {
        var cache = new OriginalCache(_configuration);
        var catalogService = CreateResultService(dbContext);
        return new ImportService(
            dbContext,
            new LibraryBootstrapper(dbContext),
            ObjectStoreFactory,
            cache,
            catalogService,
            UploaderIdentityDirectory,
            MetadataWorkSignal,
            ImportWorkSignal,
            _configuration,
            NullLogger<ImportService>.Instance);
    }

    public CacheBuildService CreateCacheBuildService(MediaCatalogDbContext dbContext) =>
        new(
            dbContext,
            ObjectStoreFactory,
            new OriginalCache(_configuration),
            new MediaThumbnailCache(_configuration),
            CacheBuildWorkSignal,
            _configuration,
            NullLogger<CacheBuildService>.Instance);

    public IUploaderIdentityDirectory UploaderIdentityDirectory { get; } = new TestUploaderIdentityDirectory();

    public static string UploaderEmail(Guid userId) => $"uploader-{userId:N}@example.test";

    public ValueTask DisposeAsync()
    {
        _storageSummaryCache.Dispose();
        if (Directory.Exists(RootDirectory)) Directory.Delete(RootDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestUploaderIdentityDirectory : IUploaderIdentityDirectory
{
    public Task<string> GetEmailAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(MediaTestHarness.UploaderEmail(userId));
    }
}

/// <summary>Keeps success-focused infrastructure tests concise while dedicated tests assert Result failures directly.</summary>
internal sealed class MediaTestClient(MediaCatalogService service)
{
    public Task<BrowseItemsRes> QueryFilesAsync(Guid userId, FileQueryOptions options, CancellationToken cancellationToken) =>
        Unwrap(service.QueryFilesAsync(userId, options, cancellationToken));

    public Task<BrowseItemsRes> BrowseTrashAsync(Guid userId, string? cursor, int limit, string type, string sort, CancellationToken cancellationToken) =>
        Unwrap(service.BrowseTrashAsync(userId, cursor, limit, type, sort, cancellationToken));

    public Task<BrowseItemsRes> BrowseFavoriteFoldersAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken) =>
        Unwrap(service.BrowseFavoriteFoldersAsync(userId, cursor, limit, cancellationToken));

    public Task<BrowseItemsRes> BrowsePhotosAsync(Guid userId, string scope, Guid? folderId, string? cursor, int limit, string type, bool favorite, string sort, int? year, string? captureDate, CancellationToken cancellationToken) =>
        Unwrap(service.BrowsePhotosAsync(userId, scope, folderId, cursor, limit, type, favorite, sort, year, captureDate, cancellationToken));

    public Task<BrowseItemsRes> BrowsePhotosAtDateAsync(Guid userId, string seekDate, int limit, CancellationToken cancellationToken) =>
        Unwrap(service.BrowsePhotosAsync(userId, "folder", null, null, limit, "all", false, "captured_desc", null, null, seekDate, null, cancellationToken));

    public Task<PhotoTimelineYearsRes> BrowsePhotoYearsAsync(Guid userId, string scope, Guid? folderId, string type, bool favorite, CancellationToken cancellationToken) =>
        Unwrap(service.BrowsePhotoYearsAsync(userId, scope, folderId, type, favorite, cancellationToken));

    public Task<PhotoTimelineAnchorsRes> BrowsePhotoTimelineAnchorsAsync(Guid userId, string scope, Guid? folderId, string type, bool favorite, string sort, int? year, string? captureDate, Guid? tagId, CancellationToken cancellationToken) =>
        Unwrap(service.BrowsePhotoTimelineAnchorsAsync(userId, scope, folderId, type, favorite, sort, year, captureDate, tagId, cancellationToken));

    public Task<BrowseItemsRes> SearchAsync(Guid userId, string query, string scope, string photoScope, string type, string? cursor, int limit, Guid? tagId, CancellationToken cancellationToken) =>
        Unwrap(service.SearchAsync(userId, query, scope, photoScope, type, cursor, limit, tagId, cancellationToken));

    public Task<TrashSummaryRes> GetTrashSummaryAsync(Guid userId, CancellationToken cancellationToken) =>
        Unwrap(service.GetTrashSummaryAsync(userId, cancellationToken));

    public Task<StorageSummaryRes> GetStorageSummaryAsync(Guid userId, bool refresh, CancellationToken cancellationToken) =>
        Unwrap(service.GetStorageSummaryAsync(userId, refresh, cancellationToken));

    public Task<MediaItemRes> CreateFolderAsync(Guid userId, CreateFolderReq request, CancellationToken cancellationToken) =>
        Unwrap(service.CreateFolderAsync(userId, request, cancellationToken));

    public Task<MediaItemRes> GetItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken) =>
        Unwrap(service.GetItemAsync(userId, itemId, cancellationToken));

    public Task<MediaItemRes> RenameItemAsync(Guid userId, Guid itemId, RenameItemReq request, CancellationToken cancellationToken) =>
        Unwrap(service.RenameItemAsync(userId, itemId, request, cancellationToken));

    public Task<MediaItemRes> MoveItemAsync(Guid userId, Guid itemId, MoveItemReq request, CancellationToken cancellationToken) =>
        Unwrap(service.MoveItemAsync(userId, itemId, request, cancellationToken));

    public Task<MediaItemRes> CopyFileAsync(Guid userId, Guid itemId, CopyFileReq request, CancellationToken cancellationToken) =>
        Unwrap(service.CopyFileAsync(userId, itemId, request, cancellationToken));

    public Task<MediaItemRes> SetFavoriteAsync(Guid userId, Guid itemId, SetFavoriteReq request, CancellationToken cancellationToken) =>
        Unwrap(service.SetFavoriteAsync(userId, itemId, request, cancellationToken));

    public Task<MediaItemRes> TrashItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken) =>
        Unwrap(service.TrashItemAsync(userId, itemId, cancellationToken));

    public Task<MediaItemRes> RestoreItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken) =>
        Unwrap(service.RestoreItemAsync(userId, itemId, cancellationToken));

    public Task<ClearTrashRes> ClearTrashAsync(Guid userId, ClearTrashReq request, CancellationToken cancellationToken) =>
        Unwrap(service.ClearTrashAsync(userId, request, cancellationToken));

    public Task<CreateUploadRes> CreateUploadAsync(Guid userId, CreateUploadReq request, CancellationToken cancellationToken) =>
        Unwrap(service.CreateUploadAsync(userId, request, cancellationToken));

    public Task<UploadProgressRes> AppendUploadChunkAsync(Guid userId, Guid uploadId, long start, long end, long total, Stream content, CancellationToken cancellationToken) =>
        Unwrap(service.AppendUploadChunkAsync(userId, uploadId, start, end, total, content, cancellationToken));

    public async Task<MediaItemRes> CompleteUploadAsync(Guid userId, Guid uploadId, CompleteUploadReq request, CancellationToken cancellationToken) =>
        (await Unwrap(service.CompleteUploadAsync(userId, uploadId, request, cancellationToken))).Item;

    public Task<MediaItemRes> RetryMetadataAsync(Guid userId, Guid itemId, CancellationToken cancellationToken) =>
        Unwrap(service.RetryMetadataAsync(userId, itemId, cancellationToken));

    public Task<MediaDownloadContent> PrepareDownloadAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        Unwrap(service.PrepareDownloadAsync(userId, itemIds, cancellationToken));

    public Task<DownloadPreviewRes> PreviewDownloadAsync(Guid userId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        Unwrap(service.PreviewDownloadAsync(userId, itemIds, cancellationToken));

    public Task<MediaDownloadContent> PrepareFolderDownloadAsync(Guid userId, Guid folderId, CancellationToken cancellationToken) =>
        Unwrap(service.PrepareFolderDownloadAsync(userId, folderId, cancellationToken));

    public Task<DownloadPreviewRes> PreviewFolderDownloadAsync(Guid userId, Guid folderId, CancellationToken cancellationToken) =>
        Unwrap(service.PreviewFolderDownloadAsync(userId, folderId, cancellationToken));

    public Task<MediaDownloadContent> PrepareAlbumDownloadAsync(Guid userId, Guid albumId, CancellationToken cancellationToken) =>
        Unwrap(service.PrepareAlbumDownloadAsync(userId, albumId, cancellationToken));

    public Task<MediaBinaryContent> OpenContentAsync(Guid userId, Guid itemId, long? rangeStart, long? rangeEnd, CancellationToken cancellationToken) =>
        Unwrap(service.OpenContentAsync(userId, itemId, rangeStart, rangeEnd, cancellationToken));

    public Task<MediaBinaryContent> OpenThumbnailAsync(Guid userId, Guid itemId, string size, CancellationToken cancellationToken) =>
        Unwrap(service.OpenThumbnailAsync(userId, itemId, size, cancellationToken));

    private static async Task<T> Unwrap<T>(Task<Result<T>> operation)
    {
        var result = await operation;
        if (result.IsSuccess) return result.SuccessValue;
        if (result.ErrorValue.ErrorCode == MediaErrors.NotFoundCode) throw new KeyNotFoundException(result.ErrorValue.Message);
        throw new ArgumentException(result.ErrorValue.Message);
    }
}

internal sealed class TestObjectStoreFactory : IObjectStoreFactory
{
    private readonly Dictionary<string, byte[]> _originals = new(StringComparer.Ordinal);

    public bool FailAfterPersistingNextPut { get; set; }
    public bool FailNextPermanentDelete { get; set; }
    public int PutCalls { get; private set; }
    public int PermanentDeleteCalls { get; private set; }
    public int ReconciliationInventoryCalls { get; private set; }
    public int OpenCalls { get; private set; }
    public int RecoveryManifestPutCalls { get; private set; }
    public Dictionary<string, RecoveryManifestData> RecoveryManifests { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> OriginalUploaderEmails { get; } = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, byte[]> Originals => _originals;
    public TaskCompletionSource? RecoveryManifestPutStarted { get; private set; }
    public TaskCompletionSource? ReleaseRecoveryManifestPut { get; private set; }
    public TaskCompletionSource PermanentDeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void RemoveOriginal(string sha256) => _originals.Remove(sha256);
    public void AddUntrackedOriginal(string sha256, byte[] bytes) => _originals[sha256] = bytes;
    public void BlockRecoveryManifestPut()
    {
        RecoveryManifestPutStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReleaseRecoveryManifestPut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public IObjectStore Create() => new TestObjectStore(this);

    private sealed class TestObjectStore(TestObjectStoreFactory owner) : IObjectStore
    {
        public async Task PutOriginalIfMissingAsync(string sha256, Stream content, long sizeBytes, string mimeType, string originalName, Guid uploaderId, string uploaderEmail, DateTime uploadedAt, CancellationToken cancellationToken)
        {
            owner.PutCalls++;
            if (owner._originals.ContainsKey(sha256)) return;
            content.Position = 0;
            await using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            owner._originals.Add(sha256, copy.ToArray());
            owner.OriginalUploaderEmails.Add(sha256, uploaderEmail);
            if (owner.FailAfterPersistingNextPut)
            {
                owner.FailAfterPersistingNextPut = false;
                throw new IOException("Simulated S3 failure after the original write.");
            }
        }

        public Task<ObjectReadResult> OpenOriginalAsync(string sha256, long? rangeStart, long? rangeEnd, CancellationToken cancellationToken)
        {
            owner.OpenCalls++;
            if (!owner._originals.TryGetValue(sha256, out var bytes)) throw new FileNotFoundException("Original was not found.", sha256);
            var start = rangeStart ?? 0;
            var end = rangeEnd ?? (bytes.Length - 1L);
            var length = Math.Max(0, end - start + 1);
            var stream = new MemoryStream(bytes, (int)start, (int)length, writable: false, publiclyVisible: true);
            return Task.FromResult(new ObjectReadResult(stream, "application/octet-stream", length, DateTime.UtcNow));
        }

        public async Task PutRecoveryManifestIfMissingAsync(RecoveryManifestData manifest, CancellationToken cancellationToken)
        {
            owner.RecoveryManifestPutCalls++;
            owner.RecoveryManifestPutStarted?.TrySetResult();
            if (owner.ReleaseRecoveryManifestPut is { } release)
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            owner.RecoveryManifests.TryAdd(manifest.Sha256, manifest);
        }

        public Task<bool> OriginalExistsAsync(string sha256, CancellationToken cancellationToken) =>
            Task.FromResult(owner._originals.ContainsKey(sha256));

        public Task DeleteOriginalPermanentlyAsync(string sha256, CancellationToken cancellationToken)
        {
            owner.PermanentDeleteCalls++;
            owner.PermanentDeleteStarted.TrySetResult();
            if (owner.FailNextPermanentDelete)
            {
                owner.FailNextPermanentDelete = false;
                throw new IOException("Simulated permanent S3 deletion failure.");
            }
            owner._originals.Remove(sha256);
            owner.OriginalUploaderEmails.Remove(sha256);
            owner.RecoveryManifests.Remove(sha256);
            return Task.CompletedTask;
        }

        public Task<StorageInventory> ListOriginalsForReconciliationAsync(CancellationToken cancellationToken)
        {
            var inventory = ListInventory();
            return Task.FromResult(new StorageInventory(
                inventory,
                new StorageInventorySummary(
                    inventory.Length,
                    inventory.Length,
                    inventory.Sum(item => item.VersionBytes),
                    0,
                    0,
                    0,
                    0)));
        }

        private StorageOriginalDescriptor[] ListInventory()
        {
            owner.ReconciliationInventoryCalls++;
            return owner._originals
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new StorageOriginalDescriptor(entry.Key, entry.Value.LongLength))
                .ToArray();
        }

        public void Dispose()
        {
        }
    }
}
