using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class StorageHardeningTests
{
    [Fact]
    public void Backup_strategy_defaults_keep_recovery_manifests_on()
    {
        var options = BackupStrategyOptions.FromConfiguration(new ConfigurationBuilder().Build());

        options.RecoveryManifestsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Developer_reconciliation_lists_untracked_s3_originals_as_opaque_candidates_and_cleans_only_selected_one()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var untrackedHash = new string('a', 64);
        harness.ObjectStoreFactory.AddUntrackedOriginal(untrackedHash, Encoding.UTF8.GetBytes("untracked original"));

        await using var dbContext = harness.CreateDbContext();
        var reconciliation = new StorageReconciliationService(dbContext, harness.ObjectStoreFactory, new OriginalCache(harness.Configuration), harness.Configuration, NullLogger<StorageReconciliationService>.Instance);
        var report = await reconciliation.ReconcileAsync(CancellationToken.None);

        report.UntrackedS3OriginalCount.Should().Be(1);
        report.StorageInventory.OriginalKeyCount.Should().Be(1);
        report.StorageInventory.OriginalVersionCount.Should().Be(1);
        report.StorageInventory.OriginalVersionBytes.Should().Be("untracked original"u8.Length);
        report.StorageInventory.RecoveryManifestKeyCount.Should().Be(0);
        report.StorageInventory.DeleteMarkerCount.Should().Be(0);
        report.CleanupCandidates.Should().ContainSingle(candidate => candidate.Kind == StorageCleanupCandidate.UntrackedS3OriginalKind);
        var candidate = report.CleanupCandidates.Single();
        candidate.SizeBytes.Should().Be("untracked original"u8.Length);
        candidate.Id.Should().NotBeEmpty();

        var rejected = await reconciliation.CleanupAsync(new CleanupStorageInconsistenciesReq
        {
            ExpectedCandidateCount = 1,
            CandidateIds = [candidate.Id]
        }, "verified-test-backup", CancellationToken.None);
        rejected.IsFailure.Should().BeTrue();
        harness.ObjectStoreFactory.Originals.Should().ContainKey(untrackedHash);

        var staleSize = await reconciliation.CleanupAsync(new CleanupStorageInconsistenciesReq
        {
            Confirmed = true,
            ConfirmationPhrase = "DELETE INCONSISTENCIES",
            ExpectedCandidateCount = 1,
            ExpectedCandidateBytes = candidate.SizeBytes + 1,
            CandidateIds = [candidate.Id]
        }, "verified-test-backup", CancellationToken.None);
        staleSize.IsFailure.Should().BeTrue();
        harness.ObjectStoreFactory.PermanentDeleteCalls.Should().Be(0);

        var cleanup = await reconciliation.CleanupAsync(new CleanupStorageInconsistenciesReq
        {
            Confirmed = true,
            ConfirmationPhrase = "DELETE INCONSISTENCIES",
            ExpectedCandidateCount = 1,
            ExpectedCandidateBytes = candidate.SizeBytes,
            CandidateIds = [candidate.Id]
        }, "verified-test-backup", CancellationToken.None);

        cleanup.IsSuccess.Should().BeTrue();
        cleanup.SuccessValue.DeletedCandidateCount.Should().Be(1);
        cleanup.SuccessValue.RemainingCandidates.Should().BeEmpty();
        harness.ObjectStoreFactory.Originals.Should().NotContainKey(untrackedHash);
    }

    [Fact]
    public async Task Reconciliation_never_offers_stored_bytes_for_cleanup_when_a_file_version_still_references_the_hash()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("referenced original with a damaged blob row");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "referenced.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(
            userId,
            upload.Id,
            0,
            bytes.Length - 1,
            bytes.Length,
            new MemoryStream(bytes),
            CancellationToken.None);
        await service.CompleteUploadAsync(
            userId,
            upload.Id,
            new CompleteUploadReq { Sha256 = hash },
            CancellationToken.None);

        await dbContext.Database.OpenConnectionAsync();
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM blobs WHERE sha256 = {0};", hash);
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        dbContext.ChangeTracker.Clear();

        var reconciliation = new StorageReconciliationService(
            dbContext,
            harness.ObjectStoreFactory,
            new OriginalCache(harness.Configuration),
            harness.Configuration,
            NullLogger<StorageReconciliationService>.Instance);
        var report = await reconciliation.ReconcileAsync(CancellationToken.None);

        report.Issues.Should().ContainSingle(issue => issue.Code == "referenced_original_missing_blob_row");
        report.UntrackedS3OriginalCount.Should().Be(0);
        report.CleanupCandidates.Should().BeEmpty();
        harness.ObjectStoreFactory.Originals.Should().ContainKey(hash);
    }

    [Fact]
    public async Task Reconciliation_keeps_a_failed_untracked_candidate_when_storage_deletion_succeeded_before_derivative_cleanup_failed()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var untrackedHash = new string('b', 64);
        harness.ObjectStoreFactory.AddUntrackedOriginal(untrackedHash, Encoding.UTF8.GetBytes("untracked original"));

        await using var dbContext = harness.CreateDbContext();
        var failedDerivativeCleanup = new ThrowingDerivativeCache();
        var reconciliation = new StorageReconciliationService(dbContext, harness.ObjectStoreFactory, failedDerivativeCleanup, harness.Configuration, NullLogger<StorageReconciliationService>.Instance);
        var candidate = (await reconciliation.ReconcileAsync(CancellationToken.None)).CleanupCandidates.Single();

        var failedCleanup = await reconciliation.CleanupAsync(new CleanupStorageInconsistenciesReq
        {
            Confirmed = true,
            ConfirmationPhrase = "DELETE INCONSISTENCIES",
            ExpectedCandidateCount = 1,
            CandidateIds = [candidate.Id]
        }, "verified-test-backup", CancellationToken.None);

        failedCleanup.IsSuccess.Should().BeTrue();
        failedCleanup.SuccessValue.FailedCandidateCount.Should().Be(1);
        harness.ObjectStoreFactory.Originals.Should().NotContainKey(untrackedHash);

        var refreshed = await reconciliation.ReconcileAsync(CancellationToken.None);
        refreshed.UntrackedS3OriginalCount.Should().Be(0);
        var retained = refreshed.CleanupCandidates.Should().ContainSingle(item => item.Id == candidate.Id && item.AttemptCount == 1).Which;
        retained.Error.Should().NotBeNullOrWhiteSpace();

        var retry = new StorageReconciliationService(dbContext, harness.ObjectStoreFactory, new OriginalCache(harness.Configuration), harness.Configuration, NullLogger<StorageReconciliationService>.Instance);
        var successfulCleanup = await retry.CleanupAsync(new CleanupStorageInconsistenciesReq
        {
            Confirmed = true,
            ConfirmationPhrase = "DELETE INCONSISTENCIES",
            ExpectedCandidateCount = 1,
            CandidateIds = [candidate.Id]
        }, "verified-test-backup", CancellationToken.None);

        successfulCleanup.IsSuccess.Should().BeTrue();
        successfulCleanup.SuccessValue.DeletedCandidateCount.Should().Be(1);
        successfulCleanup.SuccessValue.RemainingCandidates.Should().BeEmpty();
    }

    [Fact]
    public void Multipart_uploads_begin_strictly_above_the_direct_put_limit_and_use_a_bounded_part_count()
    {
        S3ObjectStore.DirectPutObjectLimitBytes.Should().Be(5L * 1024 * 1024 * 1024);
        S3ObjectStore.MultipartPartSize(S3ObjectStore.DirectPutObjectLimitBytes + 1).Should().BeGreaterThan(64L * 1024 * 1024 - 1);
        S3ObjectStore.MultipartPartSize(5L * 1024 * 1024 * 1024 * 1024).Should().BeGreaterThan(549_755_814L - 1);
    }

    [Fact]
    public void Recovery_manifest_uses_the_canonical_content_addressed_key()
    {
        var hash = new string('a', 64);
        S3ObjectStore.RecoveryManifestKey(hash).Should().Be($"recovery-manifests/{hash}.json");
    }

    [Fact]
    public void Database_backup_bucket_must_be_separate_from_the_originals_bucket()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MediaStorage:S3:BucketName"] = "same-bucket",
            ["MediaStorage:S3Backups:BucketName"] = "same-bucket"
        }).Build();

        var action = () => new S3DatabaseBackupStore(configuration);

        action.Should().Throw<InvalidOperationException>().WithMessage("*must differ from the originals bucket*");
    }

    [Fact]
    public async Task Original_cache_is_seeded_after_upload_then_repopulated_from_object_storage_after_eviction()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("cacheable original");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq { Name = "cache.txt", MimeType = "text/plain", SizeBytes = bytes.Length }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        var file = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = hash }, CancellationToken.None);
        await harness.CreateMetadataProcessor(dbContext).ProcessPendingBatchAsync(10, CancellationToken.None);
        var cache = new OriginalCache(harness.Configuration);
        File.Exists(cache.PathFor(hash)).Should().BeTrue();

        using (var cached = await service.OpenContentAsync(userId, file.Id, null, null, CancellationToken.None))
        using (var read = new MemoryStream())
        {
            await cached.Content.CopyToAsync(read);
            read.ToArray().Should().Equal(bytes);
        }
        harness.ObjectStoreFactory.OpenCalls.Should().Be(0);

        File.Delete(cache.PathFor(hash));
        using (var fetched = await service.OpenContentAsync(userId, file.Id, null, null, CancellationToken.None))
        using (var read = new MemoryStream())
        {
            await fetched.Content.CopyToAsync(read);
            read.ToArray().Should().Equal(bytes);
        }
        harness.ObjectStoreFactory.OpenCalls.Should().Be(1);
        File.Exists(cache.PathFor(hash)).Should().BeTrue();
    }

    [Fact]
    public async Task Original_cache_evicts_oldest_entries_when_over_its_configured_budget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"media-explorer-cache-test-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MediaStorage:LocalDisk:OriginalCacheDirectory"] = root,
                ["MediaStorage:LocalDisk:OriginalCacheMaxBytes"] = "10"
            }).Build();
            var cache = new OriginalCache(configuration);
            var first = new string('a', 64);
            var second = new string('b', 64);
            await cache.EnsureAsync(first, 8, (destination, token) => destination.WriteAsync(new byte[8], token).AsTask(), CancellationToken.None);
            await Task.Delay(20);
            await cache.EnsureAsync(second, 8, (destination, token) => destination.WriteAsync(new byte[8], token).AsTask(), CancellationToken.None);

            File.Exists(cache.PathFor(first)).Should().BeFalse();
            File.Exists(cache.PathFor(second)).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_reports_missing_originals_without_deleting_catalog_rows()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = MetadataFixtures.PngWithoutExif();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var folder = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Recovery" }, CancellationToken.None);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq { Name = "backup.jpg", MimeType = "image/jpeg", SizeBytes = bytes.Length, ParentId = folder.Id }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        var file = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = hash }, CancellationToken.None);
        await harness.CreateMetadataProcessor(dbContext).ProcessPendingBatchAsync(10, CancellationToken.None);

        harness.ObjectStoreFactory.RemoveOriginal(hash);
        var reconciliation = new StorageReconciliationService(dbContext, harness.ObjectStoreFactory, new OriginalCache(harness.Configuration), harness.Configuration, NullLogger<StorageReconciliationService>.Instance);
        var report = await reconciliation.ReconcileAsync(CancellationToken.None);
        report.ReferencedBlobCount.Should().Be(1);
        report.MissingOriginalCount.Should().Be(1);
        report.OrphanedCatalogBlobCount.Should().Be(0);
        (await dbContext.Blobs.CountAsync()).Should().Be(1);
    }

    private sealed class ThrowingDerivativeCache : IOriginalCache
    {
        public bool Exists(string sha256, long expectedSizeBytes) => false;
        public Task<FileStream?> TryOpenReadAsync(string sha256, long expectedSizeBytes, CancellationToken cancellationToken) => Task.FromResult<FileStream?>(null);
        public Task EnsureAsync(string sha256, long expectedSizeBytes, Func<Stream, CancellationToken, Task> populateAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EnsureFromFileAsync(string sha256, string sourcePath, long expectedSizeBytes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string sha256, CancellationToken cancellationToken) => throw new IOException("Simulated cache cleanup failure.");
        public Task<OriginalCacheEvictionResult> EvictAsync(CancellationToken cancellationToken) => Task.FromResult(new OriginalCacheEvictionResult(0, 0, 0, 0, 0));
        public Task<OriginalCacheStatus> InspectAsync(CancellationToken cancellationToken) => Task.FromResult(new OriginalCacheStatus(0, 0, 0, 0));
        public Task<IOriginalCachePopulation?> TryBeginPopulationAsync(string sha256, long expectedSizeBytes, OriginalCacheAdmission admission, CancellationToken cancellationToken) => Task.FromResult<IOriginalCachePopulation?>(null);
        public string PathFor(string sha256) => sha256;
    }
}
