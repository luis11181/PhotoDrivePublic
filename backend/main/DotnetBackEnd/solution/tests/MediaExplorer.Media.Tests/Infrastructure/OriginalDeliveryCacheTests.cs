using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class OriginalDeliveryCacheTests
{
    [Fact]
    public async Task Cached_ranges_are_read_locally_without_opening_object_storage()
    {
        await using var harness = new MediaTestHarness(new Dictionary<string, string?>
        {
            ["MediaStorage:LocalDisk:OriginalCacheAutomaticMaxFileBytes"] = "1",
            ["MediaStorage:LocalDisk:OriginalCacheMaxBytes"] = "1024"
        });
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("locally cached video bytes");
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var (itemId, hash) = await UploadAsync(service, userId, "cached.mp4", bytes);
        var cache = new OriginalCache(harness.Configuration);
        await cache.EnsureAsync(
            hash,
            bytes.Length,
            (destination, token) => destination.WriteAsync(bytes, token).AsTask(),
            default);

        using var content = await service.OpenContentAsync(userId, itemId, 4, 12, default);
        await using var delivered = new MemoryStream();
        await content.Content.CopyToAsync(delivered);

        delivered.ToArray().Should().Equal(bytes[4..13]);
        content.ContentLength.Should().Be(9);
        harness.ObjectStoreFactory.OpenCalls.Should().Be(0);
    }

    [Fact]
    public async Task Complete_zero_based_playback_range_is_cached_only_after_the_transfer_finishes()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("complete playback range that is eligible for cache-through");
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var (itemId, hash) = await UploadAsync(service, userId, "playback.mp4", bytes);
        var cache = new OriginalCache(harness.Configuration);

        using (var interrupted = await service.OpenContentAsync(userId, itemId, 0, null, default))
        {
            var prefix = new byte[7];
            (await interrupted.Content.ReadAsync(prefix)).Should().Be(prefix.Length);
            cache.Exists(hash, bytes.Length).Should().BeFalse();
        }

        cache.Exists(hash, bytes.Length).Should().BeFalse();
        Directory.EnumerateFiles(Path.GetDirectoryName(cache.PathFor(hash))!, "*.tmp").Should().BeEmpty();

        using (var completed = await service.OpenContentAsync(userId, itemId, 0, null, default))
        await using (var delivered = new MemoryStream())
        {
            await completed.Content.CopyToAsync(delivered);
            delivered.ToArray().Should().Equal(bytes);
        }

        cache.Exists(hash, bytes.Length).Should().BeTrue();
        harness.ObjectStoreFactory.OpenCalls.Should().Be(2);
    }

    [Fact]
    public async Task Partial_playback_ranges_never_create_partial_cache_entries()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("seeked playback reads only part of this original");
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var (itemId, hash) = await UploadAsync(service, userId, "seek.mp4", bytes);
        var cache = new OriginalCache(harness.Configuration);

        using var content = await service.OpenContentAsync(userId, itemId, 5, 14, default);
        await using var delivered = new MemoryStream();
        await content.Content.CopyToAsync(delivered);

        delivered.ToArray().Should().Equal(bytes[5..15]);
        cache.Exists(hash, bytes.Length).Should().BeFalse();
        var cacheDirectory = Path.GetDirectoryName(cache.PathFor(hash))!;
        if (Directory.Exists(cacheDirectory)) Directory.EnumerateFiles(cacheDirectory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Automatic_limit_skips_large_reads_but_an_explicit_download_caches_the_complete_file()
    {
        await using var harness = new MediaTestHarness(new Dictionary<string, string?>
        {
            ["MediaStorage:LocalDisk:OriginalCacheAutomaticMaxFileBytes"] = "8",
            ["MediaStorage:LocalDisk:OriginalCacheMaxBytes"] = "128"
        });
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("larger than the automatic cache admission limit");
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var (itemId, hash) = await UploadAsync(service, userId, "large.mp4", bytes);
        var cache = new OriginalCache(harness.Configuration);

        using (var playback = await service.OpenContentAsync(userId, itemId, 0, null, default))
        await using (var playbackBytes = new MemoryStream())
        {
            await playback.Content.CopyToAsync(playbackBytes);
            playbackBytes.ToArray().Should().Equal(bytes);
        }
        cache.Exists(hash, bytes.Length).Should().BeFalse();

        var download = await service.PrepareDownloadAsync(userId, [itemId], default);
        await using var downloadBytes = new MemoryStream();
        await download.WriteToAsync(downloadBytes, default);

        downloadBytes.ToArray().Should().Equal(bytes);
        cache.Exists(hash, bytes.Length).Should().BeTrue();
        harness.ObjectStoreFactory.OpenCalls.Should().Be(2);
    }

    [Fact]
    public async Task Zip_download_promotes_each_complete_missing_original_into_the_cache()
    {
        await using var harness = new MediaTestHarness(new Dictionary<string, string?>
        {
            ["MediaStorage:LocalDisk:OriginalCacheAutomaticMaxFileBytes"] = "1",
            ["MediaStorage:LocalDisk:OriginalCacheMaxBytes"] = "1024"
        });
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var firstBytes = Encoding.UTF8.GetBytes("first zip original");
        var secondBytes = Encoding.UTF8.GetBytes("second zip original");
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var (firstId, firstHash) = await UploadAsync(service, userId, "first.txt", firstBytes);
        var (secondId, secondHash) = await UploadAsync(service, userId, "second.txt", secondBytes);
        var cache = new OriginalCache(harness.Configuration);

        var download = await service.PrepareDownloadAsync(userId, [firstId, secondId], default);
        await using var destination = new MemoryStream();
        await download.WriteToAsync(destination, default);

        cache.Exists(firstHash, firstBytes.Length).Should().BeTrue();
        cache.Exists(secondHash, secondBytes.Length).Should().BeTrue();
        harness.ObjectStoreFactory.OpenCalls.Should().Be(2);
    }

    [Fact]
    public async Task Complete_download_larger_than_the_total_cache_capacity_streams_without_caching()
    {
        await using var harness = new MediaTestHarness(new Dictionary<string, string?>
        {
            ["MediaStorage:LocalDisk:OriginalCacheAutomaticMaxFileBytes"] = "4",
            ["MediaStorage:LocalDisk:OriginalCacheMaxBytes"] = "8"
        });
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("this original cannot fit");
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var (itemId, hash) = await UploadAsync(service, userId, "oversized.bin", bytes);
        var cache = new OriginalCache(harness.Configuration);

        var download = await service.PrepareDownloadAsync(userId, [itemId], default);
        await using var delivered = new MemoryStream();
        await download.WriteToAsync(delivered, default);

        delivered.ToArray().Should().Equal(bytes);
        cache.Exists(hash, bytes.Length).Should().BeFalse();
    }

    private static async Task<(Guid ItemId, string Hash)> UploadAsync(
        MediaTestClient service,
        Guid userId,
        string name,
        byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = name,
            MimeType = name.EndsWith(".mp4", StringComparison.Ordinal) ? "video/mp4" : "application/octet-stream",
            SizeBytes = bytes.Length
        }, default);
        await service.AppendUploadChunkAsync(
            userId,
            upload.Id,
            0,
            bytes.Length - 1,
            bytes.Length,
            new MemoryStream(bytes),
            default);
        var item = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = hash }, default);
        return (item.Id, hash);
    }
}
