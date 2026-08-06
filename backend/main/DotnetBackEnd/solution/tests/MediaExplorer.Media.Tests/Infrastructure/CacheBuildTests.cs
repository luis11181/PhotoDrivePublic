using System.Security.Cryptography;
using FluentAssertions;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class CacheBuildTests
{
    [Fact]
    public async Task Default_cache_build_restores_grid_thumbnail_and_keeps_original()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = MetadataFixtures.ValidExifJpeg();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using var dbContext = harness.CreateDbContext();
        await UploadAsync(harness, dbContext, userId, bytes);
        DeleteCaches(harness, sha256);
        var service = harness.CreateCacheBuildService(dbContext);
        var request = new PreviewCacheBuildReq { KeepOriginals = true };

        var previewResult = await service.PreviewAsync(request, CancellationToken.None);
        previewResult.IsSuccess.Should().BeTrue();
        var preview = previewResult.SuccessValue;
        preview.SelectedItemCount.Should().Be(1);
        preview.QueuedItemCount.Should().Be(1);
        preview.MissingThumbnailCount.Should().Be(1);
        preview.MissingOriginalCount.Should().Be(1);
        preview.DownloadBytes.Should().Be(bytes.Length);

        var jobResult = await service.StartAsync(userId, Start(request, preview), CancellationToken.None);
        jobResult.IsSuccess.Should().BeTrue();
        (await service.ProcessPendingBatchAsync(2, CancellationToken.None)).Should().Be(1);
        var completed = await service.GetAsync(jobResult.SuccessValue.Id, CancellationToken.None);

        completed.SuccessValue.State.Should().Be("completed");
        File.Exists(Path.Combine(harness.RootDirectory, "blobs", "sha256", sha256)).Should().BeTrue();
        File.Exists(Path.Combine(harness.RootDirectory, "thumbnails", "sha256", sha256, "320.webp")).Should().BeTrue();

        var options = (await service.GetOptionsAsync(CancellationToken.None)).SuccessValue;
        options.CatalogFileCount.Should().Be(1);
        options.StoredOriginalBytes.Should().Be(bytes.Length);
        options.CatalogMediaItemCount.Should().Be(1);
        options.DistinctOriginalCount.Should().Be(1);
        options.GridThumbnailCount.Should().Be(1);
        options.CachedOriginalCount.Should().Be(1);

        Directory.Delete(Path.Combine(harness.RootDirectory, "thumbnails", "sha256", sha256), recursive: true);
        var nextPreview = (await service.PreviewAsync(request, CancellationToken.None)).SuccessValue;
        var nextJob = await service.StartAsync(userId, Start(request, nextPreview), CancellationToken.None);

        nextJob.IsSuccess.Should().BeTrue();
        (await service.ListAsync(CancellationToken.None)).SuccessValue.Should().ContainSingle(item => item.Id == nextJob.SuccessValue.Id);
    }

    [Fact]
    public async Task Thumbnail_only_build_removes_temporary_original()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = MetadataFixtures.ValidExifJpeg();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using var dbContext = harness.CreateDbContext();
        await UploadAsync(harness, dbContext, userId, bytes);
        DeleteCaches(harness, sha256);
        var service = harness.CreateCacheBuildService(dbContext);
        var request = new PreviewCacheBuildReq { KeepOriginals = false };
        var preview = (await service.PreviewAsync(request, CancellationToken.None)).SuccessValue;

        var job = (await service.StartAsync(userId, Start(request, preview), CancellationToken.None)).SuccessValue;
        (await service.ProcessPendingBatchAsync(2, CancellationToken.None)).Should().Be(1);

        (await service.GetAsync(job.Id, CancellationToken.None)).SuccessValue.State.Should().Be("completed");
        File.Exists(Path.Combine(harness.RootDirectory, "blobs", "sha256", sha256)).Should().BeFalse();
        File.Exists(Path.Combine(harness.RootDirectory, "thumbnails", "sha256", sha256, "320.webp")).Should().BeTrue();
        Directory.EnumerateFiles(Path.Combine(harness.StagingDirectory, "cache-build")).Should().BeEmpty();
    }

    private static StartCacheBuildReq Start(PreviewCacheBuildReq request, CacheBuildPreviewRes preview) => new()
    {
        KeepOriginals = request.KeepOriginals,
        ExpectedSelectedItemCount = preview.SelectedItemCount,
        ExpectedQueuedItemCount = preview.QueuedItemCount,
        ExpectedDownloadBytes = preview.DownloadBytes
    };

    private static async Task UploadAsync(MediaTestHarness harness, MediaCatalogDbContext dbContext, Guid userId, byte[] bytes)
    {
        var media = harness.CreateService(dbContext);
        var upload = await media.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "cache-build.jpg",
            MimeType = "image/jpeg",
            SizeBytes = bytes.Length,
            UseDefaultDocumentsFolder = true
        }, CancellationToken.None);
        await media.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await media.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = sha256 }, CancellationToken.None);
        await harness.CreateMetadataProcessor(dbContext).ProcessPendingBatchAsync(10, CancellationToken.None);
    }

    private static void DeleteCaches(MediaTestHarness harness, string sha256)
    {
        var original = Path.Combine(harness.RootDirectory, "blobs", "sha256", sha256);
        if (File.Exists(original)) File.Delete(original);
        var thumbnails = Path.Combine(harness.RootDirectory, "thumbnails", "sha256", sha256);
        if (Directory.Exists(thumbnails)) Directory.Delete(thumbnails, recursive: true);
    }
}
