using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class StorageHardeningTests
{
    [Fact]
    public void Backup_strategy_defaults_keep_recovery_manifests_on_and_portable_exports_off()
    {
        var options = BackupStrategyOptions.FromConfiguration(new ConfigurationBuilder().Build());

        options.RecoveryManifestsEnabled.Should().BeTrue();
        options.PortableJsonExportsEnabled.Should().BeFalse();
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
    public void Catalog_backup_uses_the_snapshot_timestamp_as_its_stable_remote_backup_set_id()
    {
        S3CatalogBackupStore.CatalogBackupTimestamp("/var/backups/catalog-20260716T153045123Z.db")
            .Should().Be("20260716T153045123Z");
    }

    [Fact]
    public async Task Users_backup_is_restore_verified_and_covers_catalog_user_references()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        await CreateUsersDatabaseAsync(harness.UsersDatabasePath, userId);

        await using var catalog = harness.CreateDbContext();
        var service = harness.CreateService(catalog);
        await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Users backup coverage" }, CancellationToken.None);

        var usersBackup = new UsersBackupService(harness.Configuration);
        var verification = await usersBackup.CreateAndVerifyAsync("20260718T120000000Z", CancellationToken.None);
        verification.UsersExportPath.Should().NotBeNull();
        var usersExportPath = verification.UsersExportPath!;

        File.Exists(verification.SnapshotPath).Should().BeTrue();
        File.Exists(usersExportPath).Should().BeTrue();
        File.Exists(Path.Combine(Path.GetDirectoryName(usersExportPath)!, "users-v1.sha256")).Should().BeTrue();
        File.Exists(Path.Combine(Path.GetDirectoryName(usersExportPath)!, "RECOVERY-README.md")).Should().BeTrue();
        await AssertPortableChecksumAsync(usersExportPath, "users-v1.sha256");
        verification.UserCount.Should().Be(1);
        verification.RoleCount.Should().Be(1);
        verification.PermissionCount.Should().Be(1);
        verification.UserSessionCount.Should().Be(1);
        verification.UserTokenCount.Should().Be(1);
        (await File.ReadAllTextAsync(usersExportPath)).Should().Contain("\"recordType\":\"user\"");
        var catalogVerification = await new CatalogBackupService(catalog, harness.Configuration)
            .CreateAndVerifyAsync("20260718T120000000Z", CancellationToken.None);
        await CatalogBackupPipeline.VerifyCatalogUsersCoverageAsync(
            catalogVerification.SnapshotPath,
            verification.SnapshotPath,
            CancellationToken.None);
        S3CatalogBackupStore.BuildArtifacts(catalogVerification, verification, "20260718T120000000Z")
            .Should().HaveCount(8);
    }

    [Fact]
    public async Task Operator_restore_verifies_and_replaces_the_paired_catalog_and_users_databases()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        await CreateUsersDatabaseAsync(harness.UsersDatabasePath, userId);
        const string backupSetId = "20260718T123000000Z";

        string catalogSnapshot;
        string usersSnapshot;
        Guid retainedFolderId;
        await using (var catalog = harness.CreateDbContext())
        {
            var service = harness.CreateService(catalog);
            retainedFolderId = (await service.CreateFolderAsync(
                userId,
                new CreateFolderReq { Name = "Retained before restore" },
                CancellationToken.None)).Id;
            catalogSnapshot = (await new CatalogBackupService(catalog, harness.Configuration)
                .CreateAndVerifyAsync(backupSetId, CancellationToken.None)).SnapshotPath;
            usersSnapshot = (await new UsersBackupService(harness.Configuration)
                .CreateAndVerifyAsync(backupSetId, CancellationToken.None)).SnapshotPath;
            await service.CreateFolderAsync(
                userId,
                new CreateFolderReq { Name = "Created after backup" },
                CancellationToken.None);
        }

        var restore = new OperatorDatabaseRestoreService(harness.Configuration);
        var verified = await restore.VerifyAsync(catalogSnapshot, usersSnapshot, CancellationToken.None);
        var applied = await restore.ApplyAsync(catalogSnapshot, usersSnapshot, CancellationToken.None);

        verified.BackupSetId.Should().Be(backupSetId);
        applied.RollbackDirectory.Should().NotBeNull();
        Directory.EnumerateFiles(applied.RollbackDirectory!).Should().Contain(path => path.EndsWith("catalog.db", StringComparison.Ordinal));
        Directory.EnumerateFiles(applied.RollbackDirectory!).Should().Contain(path => path.EndsWith("users.db", StringComparison.Ordinal));
        await using var restoredCatalog = harness.CreateDbContext();
        (await restoredCatalog.Folders.AsNoTracking().AnyAsync(folder => folder.Id == retainedFolderId)).Should().BeTrue();
        (await restoredCatalog.Folders.AsNoTracking().AnyAsync(folder => folder.Name == "Created after backup")).Should().BeFalse();
    }

    [Fact]
    public async Task Operator_restore_rejects_snapshots_from_different_backup_sets()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await CreateUsersDatabaseAsync(harness.UsersDatabasePath, Guid.NewGuid());
        string catalogSnapshot;
        await using (var catalog = harness.CreateDbContext())
        {
            catalogSnapshot = (await new CatalogBackupService(catalog, harness.Configuration)
                .CreateAndVerifyAsync("20260718T124000000Z", CancellationToken.None)).SnapshotPath;
        }
        var usersSnapshot = (await new UsersBackupService(harness.Configuration)
            .CreateAndVerifyAsync("20260718T124500000Z", CancellationToken.None)).SnapshotPath;

        var restore = new OperatorDatabaseRestoreService(harness.Configuration);
        await restore.Invoking(service => service.VerifyAsync(catalogSnapshot, usersSnapshot, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*same backup set*");
    }

    [Fact]
    public async Task Portable_exports_are_omitted_by_default_while_both_verified_sqlite_snapshots_remain()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await CreateUsersDatabaseAsync(harness.UsersDatabasePath, Guid.NewGuid());
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(harness.Configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MediaStorage:BackupStrategies:PortableJsonExportsEnabled"] = "false"
            })
            .Build();
        await using var catalog = harness.CreateDbContext();

        var catalogBackup = await new CatalogBackupService(catalog, configuration)
            .CreateAndVerifyAsync("20260718T130000000Z", CancellationToken.None);
        var usersBackup = await new UsersBackupService(configuration)
            .CreateAndVerifyAsync("20260718T130000000Z", CancellationToken.None);

        File.Exists(catalogBackup.SnapshotPath).Should().BeTrue();
        File.Exists(usersBackup.SnapshotPath).Should().BeTrue();
        catalogBackup.CatalogExportPath.Should().BeNull();
        usersBackup.UsersExportPath.Should().BeNull();
        S3CatalogBackupStore.BuildArtifacts(catalogBackup, usersBackup, "20260718T130000000Z")
            .Should().HaveCount(2)
            .And.OnlyContain(artifact => artifact.ObjectKey.StartsWith("backups/sqlite/", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_backup_bucket_must_be_separate_from_the_originals_bucket()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MediaStorage:S3:BucketName"] = "same-bucket",
            ["MediaStorage:S3Backups:BucketName"] = "same-bucket"
        }).Build();

        var action = () => new S3CatalogBackupStore(configuration);

        action.Should().Throw<InvalidOperationException>().WithMessage("*must differ from the originals bucket*");
    }

    [Fact]
    public void Recurring_backup_scheduler_uses_the_next_twice_daily_boundary_in_bogota()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

        DailyCatalogBackupHostedService.NextRunUtc(
                new DateTimeOffset(2026, 7, 16, 15, 0, 0, TimeSpan.Zero),
                timeZone,
                TimeOnly.MinValue,
                intervalHours: 12)
            .Should().Be(new DateTimeOffset(2026, 7, 16, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Recurring_backup_scheduler_moves_to_the_next_boundary_after_an_exact_run_time()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

        DailyCatalogBackupHostedService.NextRunUtc(
                new DateTimeOffset(2026, 7, 16, 17, 0, 0, TimeSpan.Zero),
                timeZone,
                TimeOnly.MinValue,
                intervalHours: 12)
            .Should().Be(new DateTimeOffset(2026, 7, 17, 5, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Remote_verified_checkpoint_is_fresh_for_less_than_twelve_hours_and_invalid_or_stale_otherwise()
    {
        await using var harness = new MediaTestHarness();
        var now = new DateTimeOffset(2026, 7, 20, 17, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(now);
        var checkpoint = new CatalogBackupCheckpoint(harness.Configuration, timeProvider);
        var maximumAge = TimeSpan.FromHours(12);

        checkpoint.HasFreshVerifiedBackup(maximumAge).Should().BeFalse();
        checkpoint.GetStatus(maximumAge).Should().Be(CatalogBackupCheckpointStatus.Missing);

        var checkpointPath = Path.Combine(harness.RootDirectory, "backups", "last-remote-verified.utc");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);
        await File.WriteAllTextAsync(checkpointPath, "not-a-timestamp");
        checkpoint.HasFreshVerifiedBackup(maximumAge).Should().BeFalse();

        await checkpoint.MarkVerifiedAsync(CancellationToken.None);
        checkpoint.HasFreshVerifiedBackup(maximumAge).Should().BeTrue();
        var current = checkpoint.GetStatus(maximumAge);
        current.LastRemoteVerifiedAt.Should().Be(now);
        current.Age.Should().Be(TimeSpan.Zero);
        current.IsFresh.Should().BeTrue();

        timeProvider.UtcNow = now.AddHours(12);
        checkpoint.HasFreshVerifiedBackup(maximumAge).Should().BeFalse();
        var stale = checkpoint.GetStatus(maximumAge);
        stale.LastRemoteVerifiedAt.Should().Be(now);
        stale.Age.Should().Be(maximumAge);
        stale.IsFresh.Should().BeFalse();
    }

    [Fact]
    public async Task Startup_does_not_run_backup_when_a_remote_verified_backup_is_less_than_twelve_hours_old()
    {
        await using var harness = new MediaTestHarness();
        var checkpoint = new CatalogBackupCheckpoint(harness.Configuration, TimeProvider.System);
        await checkpoint.MarkVerifiedAsync(CancellationToken.None);
        var scopeFactory = new CountingScopeFactory();
        using var hostedService = new DailyCatalogBackupHostedService(
            scopeFactory,
            harness.Configuration,
            TimeProvider.System,
            checkpoint,
            NullLogger<DailyCatalogBackupHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            scopeFactory.CreateScopeCallCount.Should().Be(0);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Startup_runs_backup_when_no_remote_verified_backup_exists_within_the_last_twelve_hours()
    {
        await using var harness = new MediaTestHarness();
        var checkpoint = new CatalogBackupCheckpoint(harness.Configuration, TimeProvider.System);
        var scopeFactory = new CountingScopeFactory();
        using var hostedService = new DailyCatalogBackupHostedService(
            scopeFactory,
            harness.Configuration,
            TimeProvider.System,
            checkpoint,
            NullLogger<DailyCatalogBackupHostedService>.Instance);

        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            await scopeFactory.FirstCall.WaitAsync(TimeSpan.FromSeconds(2));
            scopeFactory.CreateScopeCallCount.Should().Be(1);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Successful_remote_pipeline_updates_the_startup_checkpoint()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await CreateUsersDatabaseAsync(harness.UsersDatabasePath, Guid.NewGuid());
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 17, 0, 0, TimeSpan.Zero));
        var checkpoint = new CatalogBackupCheckpoint(harness.Configuration, timeProvider);
        await using var dbContext = harness.CreateDbContext();
        var pipeline = new CatalogBackupPipeline(
            new CatalogBackupService(dbContext, harness.Configuration),
            new UsersBackupService(harness.Configuration),
            new SuccessfulBackupRemoteStore(),
            checkpoint,
            new CatalogBackupExecutionLock(),
            NullLogger<CatalogBackupPipeline>.Instance);

        var result = await pipeline.RunAsync(CancellationToken.None);

        result.RemoteBackup?.Verified.Should().BeTrue();
        checkpoint.HasFreshVerifiedBackup(TimeSpan.FromHours(12)).Should().BeTrue();
    }

    [Fact]
    public async Task Failed_remote_pipeline_does_not_update_the_startup_checkpoint()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await CreateUsersDatabaseAsync(harness.UsersDatabasePath, Guid.NewGuid());
        var checkpoint = new CatalogBackupCheckpoint(
            harness.Configuration,
            new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 17, 0, 0, TimeSpan.Zero)));
        await using var dbContext = harness.CreateDbContext();
        var pipeline = new CatalogBackupPipeline(
            new CatalogBackupService(dbContext, harness.Configuration),
            new UsersBackupService(harness.Configuration),
            new FailingBackupRemoteStore(),
            checkpoint,
            new CatalogBackupExecutionLock(),
            NullLogger<CatalogBackupPipeline>.Instance);

        var result = await pipeline.RunAsync(CancellationToken.None);

        result.RemoteBackupFailure.Should().NotBeNull();
        checkpoint.HasFreshVerifiedBackup(TimeSpan.FromHours(12)).Should().BeFalse();
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
    public async Task Reconciliation_reports_missing_originals_without_deleting_catalog_rows_and_backup_restore_verification_succeeds()
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

        var backup = new CatalogBackupService(dbContext, harness.Configuration);
        var verification = await backup.CreateAndVerifyAsync(CancellationToken.None);
        verification.CatalogExportPath.Should().NotBeNull();
        var catalogExportPath = verification.CatalogExportPath!;
        File.Exists(verification.SnapshotPath).Should().BeTrue();
        File.Exists(catalogExportPath).Should().BeTrue();
        File.Exists(Path.Combine(Path.GetDirectoryName(catalogExportPath)!, "catalog-v1.sha256")).Should().BeTrue();
        File.Exists(Path.Combine(Path.GetDirectoryName(catalogExportPath)!, "RECOVERY-README.md")).Should().BeTrue();
        await AssertPortableChecksumAsync(catalogExportPath, "catalog-v1.sha256");
        verification.LibraryCount.Should().Be(1);
        verification.FolderCount.Should().Be(3);
        verification.FileCount.Should().Be(1);
        verification.BlobCount.Should().Be(1);
        verification.FileVersionCount.Should().Be(1);
        verification.FileVersionMetadataCount.Should().Be(1);
        var export = await File.ReadAllTextAsync(catalogExportPath);
        export.Should().Contain("\"systemRole\":\"media_root\"");
        export.Should().NotContain("\"recordType\":\"photo_library_item\"");
        export.Should().Contain("\"recordType\":\"file_version_metadata\"");
        export.Should().Contain("\"captureDateSource\":\"uploaded_at\"");

        var restoredPath = Path.Combine(harness.RootDirectory, "operator-restored-catalog.db");
        File.Copy(verification.SnapshotPath, restoredPath);
        await using (var restored = new MediaCatalogDbContext(
            new DbContextOptionsBuilder<MediaCatalogDbContext>()
                .UseSqlite($"Data Source={restoredPath};Foreign Keys=True")
                .Options))
        {
            var restoredFile = await restored.Files.AsNoTracking().SingleAsync(candidate => candidate.Id == file.Id);
            restoredFile.Name.Should().Be("backup.jpg");
            (await restored.FileVersions.AsNoTracking().CountAsync()).Should().Be(1);
        }

        harness.ObjectStoreFactory.RemoveOriginal(hash);
        var reconciliation = new StorageReconciliationService(dbContext, harness.ObjectStoreFactory, new OriginalCache(harness.Configuration), harness.Configuration, NullLogger<StorageReconciliationService>.Instance);
        var report = await reconciliation.ReconcileAsync(CancellationToken.None);
        report.ReferencedBlobCount.Should().Be(1);
        report.MissingOriginalCount.Should().Be(1);
        report.OrphanedCatalogBlobCount.Should().Be(0);
        (await dbContext.Blobs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Backup_verification_rejects_missing_file_version_metadata()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("metadata coverage");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "coverage.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = hash }, CancellationToken.None);
        dbContext.FileVersionMetadata.Remove(await dbContext.FileVersionMetadata.SingleAsync());
        await dbContext.SaveChangesAsync();

        var backup = new CatalogBackupService(dbContext, harness.Configuration);
        await backup.Invoking(candidate => candidate.CreateAndVerifyAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*metadata coverage check failed*");
    }

    [Fact]
    public async Task Backup_verification_rejects_catalog_foreign_key_damage()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("foreign-key coverage");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "coverage.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = hash }, CancellationToken.None);

        var verification = await new CatalogBackupService(dbContext, harness.Configuration)
            .CreateAndVerifyAsync(CancellationToken.None);
        await using (var damaged = new SqliteConnection($"Data Source={verification.SnapshotPath};Foreign Keys=False"))
        {
            await damaged.OpenAsync();
            await using var command = damaged.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF; DELETE FROM blobs;";
            await command.ExecuteNonQueryAsync();
        }

        await FluentActions.Invoking(() => CatalogBackupService.VerifyRestoreAsync(verification.SnapshotPath, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*foreign-key check failed*");
    }

    [Fact]
    public async Task Local_catalog_backup_retention_keeps_at_most_twenty_sets()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var dbContext = harness.CreateDbContext();
        var backup = new CatalogBackupService(dbContext, harness.Configuration);
        var firstSetId = string.Empty;

        for (var index = 0; index < 21; index++)
        {
            var setId = new DateTime(2026, 7, 20, 0, index, 0, DateTimeKind.Utc)
                .ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
            if (index == 0) firstSetId = setId;
            await backup.CreateAndVerifyAsync(setId, CancellationToken.None);
        }

        var catalogBackupDirectory = Path.Combine(harness.RootDirectory, "backups", "catalog");
        Directory.EnumerateFiles(catalogBackupDirectory, "catalog-*.db").Should().HaveCount(20);
        File.Exists(Path.Combine(catalogBackupDirectory, $"catalog-{firstSetId}.db")).Should().BeFalse();
        Directory.Exists(Path.Combine(catalogBackupDirectory, $"catalog-export-{firstSetId}")).Should().BeFalse();
    }

    private static async Task AssertPortableChecksumAsync(string exportPath, string checksumFileName)
    {
        var checksumPath = Path.Combine(Path.GetDirectoryName(exportPath)!, checksumFileName);
        var checksumBytes = await File.ReadAllBytesAsync(checksumPath);
        checksumBytes.Take(3).Should().NotEqual(Encoding.UTF8.GetPreamble());

        var expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(exportPath))).ToLowerInvariant();
        Encoding.UTF8.GetString(checksumBytes).Should().Be($"{expectedHash}  {Path.GetFileName(exportPath)}{Environment.NewLine}");
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

    private sealed class SuccessfulBackupRemoteStore : ICatalogBackupRemoteStore
    {
        public Task<RemoteCatalogBackupVerification> UploadAndVerifyAsync(
            CatalogBackupVerification backup,
            UsersBackupVerification usersBackup,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCatalogBackupVerification(
                S3CatalogBackupStore.CatalogBackupTimestamp(backup.SnapshotPath),
                ArtifactCount: 8,
                UploadedBytes: new FileInfo(backup.SnapshotPath).Length + new FileInfo(usersBackup.SnapshotPath).Length,
                Verified: true));
    }

    private sealed class FailingBackupRemoteStore : ICatalogBackupRemoteStore
    {
        public Task<RemoteCatalogBackupVerification> UploadAndVerifyAsync(
            CatalogBackupVerification backup,
            UsersBackupVerification usersBackup,
            CancellationToken cancellationToken) =>
            throw new IOException("Simulated remote backup failure.");
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        private int _createScopeCallCount;
        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CreateScopeCallCount => Volatile.Read(ref _createScopeCallCount);
        public Task FirstCall => _firstCall.Task;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _createScopeCallCount);
            _firstCall.TrySetResult();
            throw new InvalidOperationException("The scheduler test stopped before resolving the backup pipeline.");
        }
    }

    private static async Task CreateUsersDatabaseAsync(string databasePath, Guid userId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE users (Id TEXT PRIMARY KEY);
            CREATE TABLE roles (Id TEXT PRIMARY KEY);
            CREATE TABLE permissions (Id TEXT PRIMARY KEY);
            CREATE TABLE role_permissions (permission_id TEXT NOT NULL, role_id TEXT NOT NULL);
            CREATE TABLE user_roles (role_id TEXT NOT NULL, user_id TEXT NOT NULL);
            CREATE TABLE user_sessions (Id TEXT PRIMARY KEY);
            CREATE TABLE user_tokens (Id TEXT PRIMARY KEY);
            CREATE TABLE security_tokens (Id TEXT PRIMARY KEY);
            CREATE TABLE security_audit_events (Id TEXT PRIMARY KEY, OccurredAt TEXT);
            INSERT INTO users (Id) VALUES ($userId);
            INSERT INTO roles (Id) VALUES ('role');
            INSERT INTO permissions (Id) VALUES ('permission');
            INSERT INTO user_sessions (Id) VALUES ('session');
            INSERT INTO user_tokens (Id) VALUES ('token');
            """;
        command.Parameters.AddWithValue("$userId", userId.ToString());
        await command.ExecuteNonQueryAsync();
    }
}
