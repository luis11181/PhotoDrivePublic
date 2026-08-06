using System.Security.Cryptography;
using FluentAssertions;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class MediaMetadataTests
{
    [Fact]
    public async Task Extractor_reads_original_exif_dimensions_actual_mime_and_orientation_from_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"media-explorer-metadata-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "incorrect-extension.bin");
        try
        {
            await File.WriteAllBytesAsync(path, MetadataFixtures.ValidExifJpeg());
            var extractor = new ExternalMediaMetadataExtractor(new ConfigurationBuilder().Build());

            var result = await extractor.ExtractAsync(path, CancellationToken.None);

            result.DetectedMimeType.Should().Be("image/jpeg");
            result.MediaFormat.Should().Be("JPEG");
            result.Width.Should().Be(12);
            result.Height.Should().Be(8);
            result.Orientation.Should().Be(6);
            result.ExifDateTimeOriginal.Should().Be(new DateTime(2025, 7, 16, 14, 30, 12, DateTimeKind.Unspecified));
            result.ExifDateTimeDigitized.Should().Be(new DateTime(2025, 7, 16, 14, 31, 0, DateTimeKind.Unspecified));
            result.RawMetadataJson.Should().Contain("DateTimeOriginal");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Extractor_keeps_useful_image_facts_when_exif_date_is_malformed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"media-explorer-metadata-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "malformed.jpg");
        try
        {
            await File.WriteAllBytesAsync(path, MetadataFixtures.MalformedExifJpeg());
            var extractor = new ExternalMediaMetadataExtractor(new ConfigurationBuilder().Build());

            var result = await extractor.ExtractAsync(path, CancellationToken.None);

            result.DetectedMimeType.Should().Be("image/jpeg");
            result.Width.Should().Be(9);
            result.Height.Should().Be(6);
            result.Orientation.Should().Be(3);
            result.ExifDateTimeOriginal.Should().BeNull();
            result.RawMetadataJson.Should().Contain("not-a-date");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Extractor_reads_video_dimensions_type_and_embedded_creation_time()
    {
        var root = Path.Combine(Path.GetTempPath(), $"media-explorer-metadata-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "incorrect-video-extension.bin");
        try
        {
            await File.WriteAllBytesAsync(path, MetadataFixtures.Mp4WithCreationTime());
            var extractor = new ExternalMediaMetadataExtractor(new ConfigurationBuilder().Build());

            var result = await extractor.ExtractAsync(path, CancellationToken.None);

            result.DetectedMimeType.Should().Be("video/mp4");
            result.Width.Should().Be(16);
            result.Height.Should().Be(10);
            result.EmbeddedCreatedAt.Should().Be(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("IMG_20250716_143012.jpg", 2025, 7, 16, 14, 30, 12)]
    [InlineData("2025-07-16 14.30.12.jpg", 2025, 7, 16, 14, 30, 12)]
    [InlineData("20250716.jpg", 2025, 7, 16, 0, 0, 0)]
    public void Filename_parser_supports_documented_camera_patterns(string name, int year, int month, int day, int hour, int minute, int second)
    {
        ExternalMediaMetadataExtractor.ParseFilenameDate(name)
            .Should().Be(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified));
    }

    [Fact]
    public void Capture_precedence_is_explicit_and_stable()
    {
        var uploadedAt = new DateTime(2026, 7, 16, 18, 0, 0, DateTimeKind.Utc);
        var filename = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        var original = new DateTime(2023, 2, 3, 4, 5, 6, DateTimeKind.Unspecified);
        var digitized = new DateTime(2023, 2, 3, 4, 6, 0, DateTimeKind.Unspecified);
        var embedded = new DateTime(2022, 1, 1, 1, 1, 1, DateTimeKind.Unspecified);
        var imported = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var all = Metadata("image/jpeg", original, digitized, embedded);
        FileMetadataProcessor.ResolveCaptureDate(null, all, filename, uploadedAt, imported).Should().Be((imported, "imported_metadata"));
        FileMetadataProcessor.ResolveCaptureDate(null, all, filename, uploadedAt).Should().Be((original, "exif_datetime_original"));
        FileMetadataProcessor.ResolveCaptureDate(null, Metadata("image/jpeg", null, digitized, embedded), filename, uploadedAt).Should().Be((digitized, "exif_datetime_digitized"));
        FileMetadataProcessor.ResolveCaptureDate(null, Metadata("image/jpeg", null, null, embedded), filename, uploadedAt).Should().Be((embedded, "embedded_creation_time"));
        FileMetadataProcessor.ResolveCaptureDate(null, Metadata("image/jpeg"), filename, uploadedAt).Should().Be((filename, "filename"));
        FileMetadataProcessor.ResolveCaptureDate(null, Metadata("image/jpeg"), null, uploadedAt).Should().Be((uploadedAt, "uploaded_at"));
    }

    [Fact]
    public async Task Upload_uses_byte_detected_mime_and_filename_fallback_in_media_folder()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = MetadataFixtures.PngWithoutExif();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var item = await UploadAsync(harness, dbContext, service, userId, "IMG_20250716_143012.jpg", "text/plain", bytes, useMediaFolder: true);

        item.MimeType.Should().Be("image/png");
        item.Kind.Should().Be("image");
        item.CapturedAt.Should().Be(new DateTime(2025, 7, 16, 14, 30, 12, DateTimeKind.Unspecified));
        item.CaptureDateSource.Should().Be("filename");
        item.Metadata!.Width.Should().Be(7);
        item.Metadata.Height.Should().Be(5);
        item.Metadata.State.Should().Be("succeeded");
        (await service.BrowsePhotosAsync(userId, "folder", null, null, 100, "all", false, "captured_desc", null, null, CancellationToken.None)).Items
            .Should().ContainSingle(photo => photo.Id == item.Id);
        (await dbContext.FileVersions.SingleAsync()).DeclaredMimeType.Should().Be("text/plain");
        (await dbContext.FileVersionMetadata.SingleAsync()).RawMetadataJson.Should().NotBeNullOrWhiteSpace();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var uploaderEmail = MediaTestHarness.UploaderEmail(userId);
        harness.ObjectStoreFactory.OriginalUploaderEmails[sha256].Should().Be(uploaderEmail);
        harness.ObjectStoreFactory.RecoveryManifests.Values.Single().Should().Match<RecoveryManifestData>(manifest =>
            manifest.DetectedMimeType == "image/png" && manifest.UploaderEmail == uploaderEmail);
    }

    [Fact]
    public async Task Upload_completion_returns_after_durable_commit_and_worker_finishes_side_effects()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = MetadataFixtures.ValidExifJpeg();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "queued.jpg",
            MimeType = "image/jpeg",
            SizeBytes = bytes.Length,
            UseDefaultMediaFolder = true
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        var committed = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        }, CancellationToken.None);
        var uploadRow = await dbContext.UploadSessions.SingleAsync(candidate => candidate.Id == upload.Id);

        committed.Metadata!.State.Should().Be("pending");
        harness.ObjectStoreFactory.Originals.Should().ContainSingle();
        (await dbContext.Files.CountAsync()).Should().Be(1);
        File.Exists(Path.Combine(harness.StagingDirectory, uploadRow.StagingFileName)).Should().BeTrue();

        await harness.CreateMetadataProcessor(dbContext).ProcessPendingBatchAsync(10, CancellationToken.None);
        var processed = await service.GetItemAsync(userId, committed.Id, CancellationToken.None);
        processed.Metadata!.State.Should().Be("succeeded");
        (await service.BrowsePhotosAsync(userId, "folder", null, null, 100, "all", false, "captured_desc", null, null, CancellationToken.None)).Items
            .Should().ContainSingle(photo => photo.Id == processed.Id);
        File.Exists(Path.Combine(harness.StagingDirectory, uploadRow.StagingFileName)).Should().BeFalse();
    }

    [Fact]
    public async Task Disabled_recovery_manifests_are_skipped_and_can_be_backfilled_after_enabling()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = MetadataFixtures.PngWithoutExif();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "optional-manifest.png",
            MimeType = "image/png",
            SizeBytes = bytes.Length
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        }, CancellationToken.None);
        var disabledConfiguration = new ConfigurationBuilder()
            .AddConfiguration(harness.Configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MediaStorage:BackupStrategies:RecoveryManifestsEnabled"] = "false"
            })
            .Build();
        var disabledProcessor = new FileMetadataProcessor(
            dbContext,
            new ExternalMediaMetadataExtractor(disabledConfiguration),
            harness.ObjectStoreFactory,
            new OriginalCache(disabledConfiguration),
            new MediaThumbnailCache(disabledConfiguration),
            harness.UploaderIdentityDirectory,
            disabledConfiguration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileMetadataProcessor>.Instance);

        await disabledProcessor.ProcessPendingBatchAsync(10, CancellationToken.None);

        harness.ObjectStoreFactory.RecoveryManifestPutCalls.Should().Be(0);
        (await dbContext.FileVersionMetadata.SingleAsync()).RecoveryManifestState.Should().Be("disabled");
        (await dbContext.Files.SingleAsync()).RecoveryManifestState.Should().Be("disabled");

        await harness.CreateMetadataProcessor(dbContext).ProcessPendingBatchAsync(10, CancellationToken.None);

        harness.ObjectStoreFactory.RecoveryManifestPutCalls.Should().Be(1);
        (await dbContext.FileVersionMetadata.SingleAsync()).RecoveryManifestState.Should().Be("succeeded");
    }

    [Fact]
    public async Task Metadata_worker_does_not_recreate_a_recovery_manifest_after_clear_trash()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = MetadataFixtures.PngWithoutExif();
        MediaItemRes pending;
        Guid versionId;
        TrashSummaryRes summary;

        await using (var uploadContext = harness.CreateDbContext())
        {
            var service = harness.CreateService(uploadContext);
            var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
            {
                Name = "delete-while-processing.png",
                MimeType = "image/png",
                SizeBytes = bytes.Length,
                UseDefaultMediaFolder = true
            }, CancellationToken.None);
            await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
            pending = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq
            {
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            }, CancellationToken.None);
            versionId = await uploadContext.FileVersions.Select(version => version.Id).SingleAsync();
            await service.TrashItemAsync(userId, pending.Id, CancellationToken.None);
            summary = await service.GetTrashSummaryAsync(userId, CancellationToken.None);
        }

        await using var workerContext = harness.CreateDbContext();
        await using var clearContext = harness.CreateDbContext();
        harness.ObjectStoreFactory.BlockRecoveryManifestPut();
        var processing = harness.CreateMetadataProcessor(workerContext).ProcessVersionAsync(versionId, CancellationToken.None);
        await harness.ObjectStoreFactory.RecoveryManifestPutStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clearing = harness.CreateService(clearContext).ClearTrashAsync(userId, new ClearTrashReq
        {
            Confirmed = true,
            ConfirmationPhrase = "DELETE",
            ExpectedFileCount = summary.FileCount,
            ExpectedFolderCount = summary.FolderCount,
            ExpectedOriginalCount = summary.OriginalCount,
            ExpectedOriginalBytes = summary.OriginalBytes
        }, CancellationToken.None);
        try
        {
            var earlyDeletion = await Task.WhenAny(harness.ObjectStoreFactory.PermanentDeleteStarted.Task, Task.Delay(TimeSpan.FromSeconds(1)));
            earlyDeletion.Should().NotBe(harness.ObjectStoreFactory.PermanentDeleteStarted.Task, "Clear Trash must wait while recovery-manifest creation holds the original mutation gate");
        }
        finally
        {
            harness.ObjectStoreFactory.ReleaseRecoveryManifestPut!.TrySetResult();
            await processing;
            var cleared = await clearing;
            cleared.DeletedOriginalCount.Should().Be(1);
        }

        harness.ObjectStoreFactory.RecoveryManifestPutCalls.Should().Be(1);
        harness.ObjectStoreFactory.RecoveryManifests.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_embedded_and_filename_dates_use_upload_timestamp_without_filesystem_dates()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var item = await UploadAsync(harness, dbContext, service, userId, "family-photo.png", "image/png", MetadataFixtures.PngWithoutExif());
        var version = await dbContext.FileVersions.SingleAsync();

        item.CaptureDateSource.Should().Be("uploaded_at");
        item.CapturedAt.Should().Be(version.CreatedAt);
        item.Metadata!.EmbeddedCapturedAt.Should().BeNull();
        item.Metadata.FilenameCapturedAt.Should().BeNull();
    }

    [Fact]
    public async Task Malformed_exif_date_does_not_fail_processing_and_uses_filename_fallback()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var item = await UploadAsync(harness, dbContext, service, userId, "scan_20250716.jpg", "image/jpeg", MetadataFixtures.MalformedExifJpeg());

        item.Metadata!.State.Should().Be("succeeded");
        item.Metadata.Orientation.Should().Be(3);
        item.CaptureDateSource.Should().Be("filename");
        item.CapturedAt.Should().Be(new DateTime(2025, 7, 16, 0, 0, 0, DateTimeKind.Unspecified));
    }

    [Fact]
    public async Task Photos_timeline_orders_and_paginates_by_effective_capture_date()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = MetadataFixtures.PngWithoutExif();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var oldest = await UploadAsync(harness, dbContext, service, userId, "20230102.jpg", "application/octet-stream", bytes, useMediaFolder: true);
        var older = await UploadAsync(harness, dbContext, service, userId, "20240102.jpg", "application/octet-stream", bytes, useMediaFolder: true);
        var newer = await UploadAsync(harness, dbContext, service, userId, "20250716.jpg", "application/octet-stream", bytes, useMediaFolder: true);

        var firstPage = await service.BrowsePhotosAsync(userId, "folder", null, null, 1, "all", false, "captured_desc", null, null, CancellationToken.None);
        firstPage.Items.Should().ContainSingle().Which.Id.Should().Be(newer.Id);
        firstPage.NextCursor.Should().NotBeNull();
        var secondPage = await service.BrowsePhotosAsync(userId, "folder", null, firstPage.NextCursor, 1, "all", false, "captured_desc", null, null, CancellationToken.None);
        secondPage.Items.Should().ContainSingle().Which.Id.Should().Be(older.Id);

        var oldestFirst = await service.BrowsePhotosAsync(userId, "folder", null, null, 100, "all", false, "captured_asc", null, null, CancellationToken.None);
        oldestFirst.Items[0].Id.Should().Be(oldest.Id);
        var july = await service.BrowsePhotosAsync(userId, "folder", null, null, 100, "all", false, "captured_desc", null, "2025-07", CancellationToken.None);
        july.Items.Should().ContainSingle().Which.Id.Should().Be(newer.Id);
        var day = await service.BrowsePhotosAsync(userId, "folder", null, null, 100, "all", false, "captured_desc", null, "2025-07-16", CancellationToken.None);
        day.Items.Should().ContainSingle().Which.Id.Should().Be(newer.Id);
        var eventSeek = await service.BrowsePhotosAtDateAsync(
            userId, "2025-01-01", 1, CancellationToken.None);
        eventSeek.Items.Should().ContainSingle().Which.Id.Should().Be(older.Id);
        eventSeek.PreviousCursor.Should().NotBeNull();
        eventSeek.NextCursor.Should().NotBeNull();
        var newerFromSeek = await service.BrowsePhotosAsync(
            userId, "folder", null, eventSeek.PreviousCursor, 1, "all", false, "captured_desc", null, null, CancellationToken.None);
        newerFromSeek.Items.Should().ContainSingle().Which.Id.Should().Be(newer.Id);
        (await service.BrowsePhotoYearsAsync(userId, "folder", null, "all", false, CancellationToken.None)).Years
            .Should().Equal(2025, 2024, 2023);
    }

    [Fact]
    public async Task Reprocessing_updates_one_metadata_row_without_corrupting_results()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = MetadataFixtures.ValidExifJpeg();

        Guid itemId;
        await using (var dbContext = harness.CreateDbContext())
        {
            var service = harness.CreateService(dbContext);
            var item = await UploadAsync(harness, dbContext, service, userId, "camera.jpg", "application/octet-stream", bytes);
            itemId = item.Id;
            item.CaptureDateSource.Should().Be("exif_datetime_original");
        }

        await using (var retryContext = harness.CreateDbContext())
        {
            var retryService = harness.CreateService(retryContext);
            var queued = await retryService.RetryMetadataAsync(userId, itemId, CancellationToken.None);
            queued.Metadata!.State.Should().Be("pending");
            await harness.CreateMetadataProcessor(retryContext).ProcessPendingBatchAsync(10, CancellationToken.None);
            var retried = await retryService.GetItemAsync(userId, itemId, CancellationToken.None);
            retried.CaptureDateSource.Should().Be("exif_datetime_original");
            retried.Metadata!.AttemptCount.Should().Be(2);
            (await retryContext.FileVersionMetadata.CountAsync()).Should().Be(1);
            (await retryContext.Files.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task Oriented_image_thumbnail_is_auto_rotated_for_rendering()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var bytes = MetadataFixtures.ValidExifJpeg();
        var item = await UploadAsync(harness, dbContext, service, userId, "rotated.jpg", "image/jpeg", bytes);
        var gridThumbnailPath = Path.Combine(
            harness.RootDirectory,
            "thumbnails",
            "sha256",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            "320.webp");
        File.Exists(gridThumbnailPath).Should().BeTrue("metadata processing should prewarm the grid thumbnail");
        await using var thumbnailBytes = new MemoryStream();
        using (var thumbnail = await service.OpenThumbnailAsync(userId, item.Id, "lg", CancellationToken.None))
        {
            await thumbnail.Content.CopyToAsync(thumbnailBytes);
        }
        var thumbnailPath = Path.Combine(harness.RootDirectory, "oriented-thumbnail.webp");
        await File.WriteAllBytesAsync(thumbnailPath, thumbnailBytes.ToArray());
        var extractedThumbnail = await new ExternalMediaMetadataExtractor(harness.Configuration).ExtractAsync(thumbnailPath, CancellationToken.None);

        extractedThumbnail.DetectedMimeType.Should().Be("image/webp");
        extractedThumbnail.Width.Should().Be(8);
        extractedThumbnail.Height.Should().Be(12);
        extractedThumbnail.Orientation.Should().Be(1);
    }

    [Fact]
    public async Task Video_thumbnail_extracts_a_representative_frame_as_webp()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var bytes = MetadataFixtures.Mp4WithCreationTime();
        var item = await UploadAsync(harness, dbContext, service, userId, "clip.mp4", "video/mp4", bytes);
        item.ThumbnailPath.Should().Be($"/api/v1/items/{item.Id}/thumbnail?size=md");
        var gridThumbnailPath = Path.Combine(
            harness.RootDirectory,
            "thumbnails",
            "sha256",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            "320.webp");
        File.Exists(gridThumbnailPath).Should().BeTrue("metadata processing should prewarm the grid thumbnail");

        await using var thumbnailBytes = new MemoryStream();
        using (var thumbnail = await service.OpenThumbnailAsync(userId, item.Id, "lg", CancellationToken.None))
        {
            thumbnail.ContentType.Should().Be("image/webp");
            await thumbnail.Content.CopyToAsync(thumbnailBytes);
        }
        var thumbnailPath = Path.Combine(harness.RootDirectory, "video-thumbnail.webp");
        await File.WriteAllBytesAsync(thumbnailPath, thumbnailBytes.ToArray());
        var extractedThumbnail = await new ExternalMediaMetadataExtractor(harness.Configuration).ExtractAsync(thumbnailPath, CancellationToken.None);

        extractedThumbnail.DetectedMimeType.Should().Be("image/webp");
        extractedThumbnail.Width.Should().Be(16);
        extractedThumbnail.Height.Should().Be(10);
    }

    [Fact]
    public async Task Metadata_tool_failure_keeps_committed_upload_and_allows_retry()
    {
        await using var harness = new MediaTestHarness { MetadataExtractorOverride = new ThrowingExtractor() };
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = MetadataFixtures.ValidExifJpeg();
        Guid itemId;

        await using (var failedContext = harness.CreateDbContext())
        {
            var service = harness.CreateService(failedContext);
            var item = await UploadAsync(harness, failedContext, service, userId, "failed-tool.jpg", "image/jpeg", bytes, useMediaFolder: true);
            itemId = item.Id;
            item.Metadata!.State.Should().Be("failed");
            item.Metadata.ErrorCode.Should().Be("metadata_tool_failed");
            item.Metadata.ErrorMessage.Should().NotContain("Simulated metadata tool failure");
            (await failedContext.Files.CountAsync()).Should().Be(1);
            (await failedContext.Blobs.CountAsync()).Should().Be(1);
            harness.ObjectStoreFactory.Originals.Should().ContainSingle();
        }

        harness.MetadataExtractorOverride = null;
        await using (var retryContext = harness.CreateDbContext())
        {
            var service = harness.CreateService(retryContext);
            var queued = await service.RetryMetadataAsync(userId, itemId, CancellationToken.None);
            queued.Metadata!.State.Should().Be("pending");
            await harness.CreateMetadataProcessor(retryContext).ProcessPendingBatchAsync(10, CancellationToken.None);
            var retried = await service.GetItemAsync(userId, itemId, CancellationToken.None);
            retried.Metadata!.State.Should().Be("succeeded");
            retried.MimeType.Should().Be("image/jpeg");
            retried.CaptureDateSource.Should().Be("exif_datetime_original");
            (await retryContext.FileVersionMetadata.CountAsync()).Should().Be(1);
        }
    }

    private static ExtractedMediaMetadata Metadata(
        string mimeType,
        DateTime? original = null,
        DateTime? digitized = null,
        DateTime? embedded = null) =>
        new(mimeType, "TEST", 1, 1, 1, original, digitized, embedded, "{}");

    private static async Task<MediaItemRes> UploadAsync(
        MediaTestHarness harness,
        MediaCatalogDbContext dbContext,
        MediaTestClient service,
        Guid userId,
        string name,
        string declaredMimeType,
        byte[] bytes,
        bool useMediaFolder = false)
    {
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = name,
            MimeType = declaredMimeType,
            SizeBytes = bytes.Length,
            UseDefaultMediaFolder = useMediaFolder
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var pending = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = hash }, CancellationToken.None);
        await harness.CreateMetadataProcessor(dbContext).ProcessPendingBatchAsync(10, CancellationToken.None);
        return await service.GetItemAsync(userId, pending.Id, CancellationToken.None);
    }

    private sealed class ThrowingExtractor : IMediaMetadataExtractor
    {
        public Task<ExtractedMediaMetadata> ExtractAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated metadata tool failure.");
    }
}
