using FluentAssertions;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class GoogleTakeoutImportTests
{
    [Fact]
    public async Task Sidecar_reader_normalizes_utc_description_location_and_ignores_unknown_google_fields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"media-explorer-sidecar-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "photo.jpg.supplemental-metadata.json");
        try
        {
            await File.WriteAllTextAsync(path,
                """
                {
                  "title": "photo.jpg",
                  "description": "  A family day  ",
                  "photoTakenTime": { "timestamp": "1780877198", "formatted": "ignored" },
                  "geoData": {
                    "latitude": 4.60971,
                    "longitude": -74.08175,
                    "altitude": 2640.5,
                    "latitudeSpan": 0.0,
                    "longitudeSpan": 0.0
                  },
                  "people": [{ "name": "not imported" }],
                  "url": "https://photos.example/not-persisted"
                }
                """);

            var metadata = await GoogleTakeoutSidecarReader.ReadAsync(path, CancellationToken.None);

            metadata.Title.Should().Be("photo.jpg");
            metadata.CapturedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1780877198).UtcDateTime);
            metadata.Description.Should().Be("A family day");
            metadata.Latitude.Should().Be(4.60971);
            metadata.Longitude.Should().Be(-74.08175);
            metadata.AltitudeMeters.Should().Be(2640.5);
            metadata.Warnings.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sidecar_reader_treats_the_takeout_all_zero_location_as_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"media-explorer-sidecar-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "photo.jpg.supplemental-metadata.json");
        try
        {
            await File.WriteAllTextAsync(path,
                """
                {
                  "title": "photo.jpg",
                  "photoTakenTime": { "timestamp": "1780877198" },
                  "geoData": {
                    "latitude": 0.0,
                    "longitude": 0.0,
                    "altitude": 0.0,
                    "latitudeSpan": 0.0,
                    "longitudeSpan": 0.0
                  }
                }
                """);

            var metadata = await GoogleTakeoutSidecarReader.ReadAsync(path, CancellationToken.None);

            metadata.Latitude.Should().BeNull();
            metadata.Longitude.Should().BeNull();
            metadata.AltitudeMeters.Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_is_restart_safe_preserves_normalized_metadata_and_skips_a_second_catalog_copy()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var source = Path.Combine(harness.RootDirectory, "Takeout", "Google Photos", "Photos from 2026");
        Directory.CreateDirectory(source);
        var mediaPath = Path.Combine(source, "takeout-photo.jpg");
        await File.WriteAllBytesAsync(mediaPath, MetadataFixtures.ValidExifJpeg());
        await File.WriteAllTextAsync(
            $"{mediaPath}.supplemental-metadata.json",
            """
            {
              "title": "takeout-photo.jpg",
              "description": "Imported description",
              "photoTakenTime": { "timestamp": "1780877198" },
              "geoData": {
                "latitude": 4.60971,
                "longitude": -74.08175,
                "altitude": 2640.5,
                "latitudeSpan": 0.0,
                "longitudeSpan": 0.0
              }
            }
            """);
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var imports = harness.CreateImportService(dbContext);
        var destinationFolderId = await MediaFolderIdAsync(imports, userId);
        var started = await imports.StartGoogleTakeoutAsync(userId, new CreateGoogleTakeoutImportReq
        {
            SourcePath = Path.Combine(harness.RootDirectory, "Takeout", "Google Photos"),
            DestinationFolderId = destinationFolderId,
            NameConflictPolicy = "skip"
        }, CancellationToken.None);
        started.IsSuccess.Should().BeTrue();
        started.SuccessValue.DiscoveredCount.Should().Be(1);

        (await imports.ProcessPendingBatchAsync(10, CancellationToken.None)).Should().Be(1);
        var completed = await imports.GetAsync(userId, started.SuccessValue.Id, CancellationToken.None);
        completed.SuccessValue.State.Should().Be(ImportJob.CompletedState);
        completed.SuccessValue.ImportedCount.Should().Be(1);

        await harness.CreateMetadataProcessor(dbContext).ProcessPendingBatchAsync(10, CancellationToken.None);
        var file = await dbContext.Files.SingleAsync();
        var metadata = await dbContext.FileVersionMetadata.SingleAsync();
        file.CapturedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1780877198).UtcDateTime);
        file.ParentFolderId.Should().Be(destinationFolderId);
        file.CaptureDateSource.Should().Be("imported_metadata");
        file.Description.Should().Be("Imported description");
        file.Latitude.Should().Be(4.60971);
        file.Longitude.Should().Be(-74.08175);
        metadata.ImportedCapturedAt.Should().Be(file.CapturedAt);
        var photos = await harness.CreateService(dbContext).BrowsePhotosAsync(
            userId, "folder", null, null, 100, "all", false, "captured_desc", null, null, CancellationToken.None);
        photos.Items.Should().ContainSingle(item => item.Id == file.Id);

        var repeated = await imports.StartGoogleTakeoutAsync(userId, new CreateGoogleTakeoutImportReq
        {
            SourcePath = Path.Combine(harness.RootDirectory, "Takeout", "Google Photos"),
            DestinationFolderId = destinationFolderId,
            NameConflictPolicy = "skip"
        }, CancellationToken.None);
        repeated.IsSuccess.Should().BeTrue();
        (await imports.ProcessPendingBatchAsync(10, CancellationToken.None)).Should().Be(1);
        var repeatedStatus = await imports.GetAsync(userId, repeated.SuccessValue.Id, CancellationToken.None);

        repeatedStatus.SuccessValue.DuplicateCount.Should().Be(1);
        var duplicateItems = await imports.ListItemsAsync(
            userId,
            repeated.SuccessValue.Id,
            "duplicates",
            null,
            50,
            CancellationToken.None);
        duplicateItems.SuccessValue.Items.Should().ContainSingle();
        duplicateItems.SuccessValue.Items[0].RelativePath.Should().Be("Photos from 2026/takeout-photo.jpg");
        duplicateItems.SuccessValue.Items[0].OutcomeCode.Should().Be("duplicate_active");
        duplicateItems.SuccessValue.Items[0].CatalogFileId.Should().Be(file.Id);
        var skippedItems = await imports.ListItemsAsync(
            userId,
            repeated.SuccessValue.Id,
            "skipped",
            null,
            50,
            CancellationToken.None);
        skippedItems.SuccessValue.Items.Should().ContainSingle();
        skippedItems.SuccessValue.Items[0].Name.Should().Be("takeout-photo.jpg");
        (await dbContext.Files.CountAsync()).Should().Be(1);
        harness.ObjectStoreFactory.Originals.Should().ContainSingle();
    }

    [Fact]
    public async Task Import_can_keep_a_second_catalog_file_with_identical_bytes_when_requested()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var source = Path.Combine(harness.RootDirectory, "Takeout", "Google Photos");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(
            Path.Combine(source, "same-bytes.jpg"),
            MetadataFixtures.ValidExifJpeg());
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var imports = harness.CreateImportService(dbContext);
        var destinationFolderId = await MediaFolderIdAsync(imports, userId);
        var first = await imports.StartGoogleTakeoutAsync(userId, new CreateGoogleTakeoutImportReq
        {
            SourcePath = source,
            DestinationFolderId = destinationFolderId,
            NameConflictPolicy = "keep_both"
        }, default);
        (await imports.ProcessPendingBatchAsync(10, default)).Should().Be(1);

        var second = await imports.StartGoogleTakeoutAsync(userId, new CreateGoogleTakeoutImportReq
        {
            SourcePath = source,
            DestinationFolderId = destinationFolderId,
            NameConflictPolicy = "keep_both",
            SkipIdenticalFiles = false
        }, default);
        second.SuccessValue.NameConflictPolicy.Should().Be("keep_both");
        second.SuccessValue.SkipIdenticalFiles.Should().BeFalse();
        (await imports.ProcessPendingBatchAsync(10, default)).Should().Be(1);

        var completed = await imports.GetAsync(userId, second.SuccessValue.Id, default);
        completed.SuccessValue.ImportedCount.Should().Be(1);
        completed.SuccessValue.DuplicateCount.Should().Be(0);
        completed.SuccessValue.Outcomes.Should().ContainKey("imported_reused_original");
        (await dbContext.Files.CountAsync()).Should().Be(2);
        (await dbContext.Blobs.CountAsync()).Should().Be(1);
        harness.ObjectStoreFactory.Originals.Should().ContainSingle();
        first.SuccessValue.SkipIdenticalFiles.Should().BeTrue();
    }

    [Fact]
    public async Task Import_uses_one_safe_name_conflict_policy_for_the_whole_job()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var source = Path.Combine(harness.RootDirectory, "Takeout", "Google Photos");
        Directory.CreateDirectory(Path.Combine(source, "A"));
        Directory.CreateDirectory(Path.Combine(source, "B"));
        await File.WriteAllBytesAsync(Path.Combine(source, "A", "same-name.jpg"), MetadataFixtures.ValidExifJpeg());
        await File.WriteAllBytesAsync(Path.Combine(source, "B", "same-name.jpg"), MetadataFixtures.MalformedExifJpeg());
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var imports = harness.CreateImportService(dbContext);
        var destinationFolderId = await MediaFolderIdAsync(imports, userId);
        var started = await imports.StartGoogleTakeoutAsync(userId, new CreateGoogleTakeoutImportReq
        {
            SourcePath = source,
            DestinationFolderId = destinationFolderId,
            NameConflictPolicy = "skip"
        }, CancellationToken.None);
        started.IsSuccess.Should().BeTrue();

        (await imports.ProcessPendingBatchAsync(10, CancellationToken.None)).Should().Be(2);
        var completed = await imports.GetAsync(userId, started.SuccessValue.Id, CancellationToken.None);

        completed.SuccessValue.ImportedCount.Should().Be(1);
        completed.SuccessValue.NameConflictCount.Should().Be(1);
        var warningItems = await imports.ListItemsAsync(
            userId,
            started.SuccessValue.Id,
            "warnings",
            null,
            1,
            CancellationToken.None);
        warningItems.SuccessValue.Items.Should().ContainSingle();
        warningItems.SuccessValue.NextCursor.Should().NotBeNull();
        warningItems.SuccessValue.Items.Should().OnlyContain(item => item.WarningCodes.Contains("sidecar_missing"));
        var nextWarningItems = await imports.ListItemsAsync(
            userId,
            started.SuccessValue.Id,
            "warnings",
            warningItems.SuccessValue.NextCursor,
            1,
            CancellationToken.None);
        nextWarningItems.SuccessValue.Items.Should().ContainSingle();
        nextWarningItems.SuccessValue.NextCursor.Should().BeNull();
        (await imports.ListItemsAsync(
            Guid.NewGuid(),
            started.SuccessValue.Id,
            "warnings",
            null,
            50,
            CancellationToken.None)).IsFailure.Should().BeTrue();
        (await dbContext.Files.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Import_rejects_a_symbolic_link_discovered_inside_the_selected_directory()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var outside = Path.Combine(Path.GetTempPath(), $"media-explorer-import-outside-{Guid.NewGuid():N}");
        var source = Path.Combine(harness.RootDirectory, "Takeout");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(source, "linked-content"), outside);
        try
        {
            await using var dbContext = harness.CreateDbContext();
            var imports = harness.CreateImportService(dbContext);
            var userId = Guid.NewGuid();
            var result = await imports.StartGoogleTakeoutAsync(
                userId,
                new CreateGoogleTakeoutImportReq
                {
                    SourcePath = source,
                    DestinationFolderId = await MediaFolderIdAsync(imports, userId),
                    NameConflictPolicy = "skip"
                },
                CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            (await dbContext.ImportJobs.CountAsync()).Should().Be(0);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Import_accepts_any_accessible_directory_and_uses_the_selected_catalog_folder()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var source = Path.Combine(Path.GetTempPath(), $"media-explorer-unrestricted-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "outside-root.jpg"), MetadataFixtures.ValidExifJpeg());
        try
        {
            var userId = Guid.NewGuid();
            await using var dbContext = harness.CreateDbContext();
            var catalog = harness.CreateService(dbContext);
            var destination = await catalog.CreateFolderAsync(
                userId,
                new CreateFolderReq { Name = "Takeout destination" },
                CancellationToken.None);
            var imports = harness.CreateImportService(dbContext);

            var started = await imports.StartGoogleTakeoutAsync(
                userId,
                new CreateGoogleTakeoutImportReq
                {
                    SourcePath = source,
                    DestinationFolderId = destination.Id,
                    NameConflictPolicy = "skip"
                },
                CancellationToken.None);
            started.IsSuccess.Should().BeTrue();
            started.SuccessValue.DestinationFolderId.Should().Be(destination.Id);
            started.SuccessValue.DestinationPath.Should().EndWith("Takeout destination");

            (await imports.ProcessPendingBatchAsync(10, CancellationToken.None)).Should().Be(1);
            (await dbContext.Files.SingleAsync()).ParentFolderId.Should().Be(destination.Id);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    private static async Task<Guid> MediaFolderIdAsync(ImportService imports, Guid userId)
    {
        var options = await imports.GetOptionsAsync(userId, CancellationToken.None);
        options.IsSuccess.Should().BeTrue();
        return options.SuccessValue.Destinations
            .Single(destination => destination.SystemRole == LibraryBootstrapper.MediaFolderRole)
            .Id;
    }
}
