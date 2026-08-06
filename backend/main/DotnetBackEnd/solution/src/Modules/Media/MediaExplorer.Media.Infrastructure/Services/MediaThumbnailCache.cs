using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class MediaThumbnailCache(IConfiguration configuration)
{
    internal const int GridPixels = 320;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ThumbnailLocks = new(StringComparer.Ordinal);
    private readonly string _thumbnailDirectory = Path.GetFullPath(configuration["MediaStorage:LocalDisk:ThumbnailDirectory"] ?? "media-explorer.thumbnails");
    private readonly string _imageToolPath = configuration["MediaStorage:ImageToolPath"] ?? "convert";
    private readonly string _videoThumbnailToolPath = configuration["MediaStorage:VideoThumbnailToolPath"] ?? "ffmpeg";

    public string PathFor(string sha256, int pixels) =>
        Path.Combine(_thumbnailDirectory, "sha256", sha256, $"{pixels}.webp");

    public bool Exists(string sha256, int pixels) => File.Exists(PathFor(sha256, pixels));

    public async Task EnsureAsync(
        string sha256,
        string mimeType,
        int pixels,
        Func<CancellationToken, Task<string>> sourcePathFactory,
        CancellationToken cancellationToken)
    {
        var thumbnailPath = PathFor(sha256, pixels);
        if (File.Exists(thumbnailPath)) return;

        var gate = ThumbnailLocks.GetOrAdd(thumbnailPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(thumbnailPath)) return;
            var sourcePath = await sourcePathFactory(cancellationToken);
            await GenerateAsync(mimeType, sourcePath, pixels, thumbnailPath, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task EnsureAsync(
        string sha256,
        string mimeType,
        string sourcePath,
        int pixels,
        CancellationToken cancellationToken) =>
        EnsureAsync(sha256, mimeType, pixels, _ => Task.FromResult(sourcePath), cancellationToken);

    public void RemoveAll(string sha256)
    {
        var directory = Path.Combine(_thumbnailDirectory, "sha256", sha256);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private async Task GenerateAsync(string mimeType, string sourcePath, int pixels, string thumbnailPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(thumbnailPath)!);
        var temporaryPath = $"{thumbnailPath}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            var startInfo = mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                ? VideoThumbnailStartInfo(sourcePath, pixels, temporaryPath)
                : ImageThumbnailStartInfo(sourcePath, pixels, temporaryPath);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the thumbnail process.");
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !File.Exists(temporaryPath))
            {
                throw new InvalidOperationException($"Thumbnail generation failed: {error.Trim()}");
            }
            File.Move(temporaryPath, thumbnailPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private ProcessStartInfo ImageThumbnailStartInfo(string sourcePath, int pixels, string temporaryPath)
    {
        var startInfo = ThumbnailStartInfo(_imageToolPath);
        startInfo.ArgumentList.Add($"{sourcePath}[0]");
        startInfo.ArgumentList.Add("-auto-orient");
        startInfo.ArgumentList.Add("-thumbnail");
        startInfo.ArgumentList.Add($"{pixels}x{pixels}>");
        startInfo.ArgumentList.Add("-strip");
        startInfo.ArgumentList.Add("-quality");
        startInfo.ArgumentList.Add("82");
        startInfo.ArgumentList.Add($"webp:{temporaryPath}");
        return startInfo;
    }

    private ProcessStartInfo VideoThumbnailStartInfo(string sourcePath, int pixels, string temporaryPath)
    {
        var startInfo = ThumbnailStartInfo(_videoThumbnailToolPath);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"thumbnail=100,scale='min({pixels},iw)':'min({pixels},ih)':force_original_aspect_ratio=decrease,setsar=1");
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libwebp");
        startInfo.ArgumentList.Add("-quality");
        startInfo.ArgumentList.Add("82");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("webp");
        startInfo.ArgumentList.Add(temporaryPath);
        return startInfo;
    }

    private static ProcessStartInfo ThumbnailStartInfo(string toolPath) => new(toolPath)
    {
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
}
