using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

internal interface IOriginalCache
{
    Task<FileStream?> TryOpenReadAsync(string sha256, long expectedSizeBytes, CancellationToken cancellationToken);
    Task EnsureAsync(string sha256, long expectedSizeBytes, Func<Stream, CancellationToken, Task> populateAsync, CancellationToken cancellationToken);
    Task EnsureFromFileAsync(string sha256, string sourcePath, long expectedSizeBytes, CancellationToken cancellationToken);
    Task RemoveAsync(string sha256, CancellationToken cancellationToken);
    Task<OriginalCacheEvictionResult> EvictAsync(CancellationToken cancellationToken);
    Task<OriginalCacheStatus> InspectAsync(CancellationToken cancellationToken);
    Task<IOriginalCachePopulation?> TryBeginPopulationAsync(
        string sha256,
        long expectedSizeBytes,
        OriginalCacheAdmission admission,
        CancellationToken cancellationToken);
    bool Exists(string sha256, long expectedSizeBytes);
    string PathFor(string sha256);
}

internal interface IOriginalCachePopulation : IDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    Task CompleteAsync(CancellationToken cancellationToken);
}

internal enum OriginalCacheAdmission
{
    Automatic,
    CompleteTransfer
}

internal sealed record OriginalCacheEvictionResult(long TotalBytes, int EntryCount, long CapacityBytes, int RemovedEntries, long RemovedBytes);
internal sealed record OriginalCacheStatus(long TotalBytes, int EntryCount, long CapacityBytes, long AvailableDiskBytes);

/// <summary>
/// Rebuildable local cache for immutable originals. Paths are hash-addressed and never user-visible.
/// </summary>
internal sealed class OriginalCache : IOriginalCache
{
    private const long DefaultMaxBytes = 20L * 1024 * 1024 * 1024;
    private const long DefaultAutomaticMaxFileBytes = 500L * 1024 * 1024;
    private readonly string _rootDirectory;
    private readonly long _maxBytes;
    private readonly long _automaticMaxFileBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public OriginalCache(IConfiguration configuration)
    {
        _rootDirectory = Path.GetFullPath(configuration["MediaStorage:LocalDisk:OriginalCacheDirectory"] ?? "media-explorer.cache/blobs/sha256");
        _maxBytes = ParsePositiveLong(configuration["MediaStorage:LocalDisk:OriginalCacheMaxBytes"], DefaultMaxBytes);
        _automaticMaxFileBytes = ParsePositiveLong(configuration["MediaStorage:LocalDisk:OriginalCacheAutomaticMaxFileBytes"], DefaultAutomaticMaxFileBytes);
    }

    public string PathFor(string sha256)
    {
        ValidateSha256(sha256);
        return Path.Combine(_rootDirectory, sha256);
    }

    /// <summary>
    /// Records recent use for LRU eviction. Best effort: only a file's owner may change its timestamps, and cache
    /// files left by an earlier container user (for example root-owned files on a Windows bind mount) must still
    /// be served. Such files merely look older to eviction.
    /// </summary>
    internal static void TouchForEviction(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
        }
    }

    public bool Exists(string sha256, long expectedSizeBytes)
    {
        var path = PathFor(sha256);
        return File.Exists(path) && new FileInfo(path).Length == expectedSizeBytes;
    }

    public Task<FileStream?> TryOpenReadAsync(string sha256, long expectedSizeBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(sha256);
        if (!File.Exists(path) || new FileInfo(path).Length != expectedSizeBytes)
        {
            if (File.Exists(path)) File.Delete(path);
            return Task.FromResult<FileStream?>(null);
        }

        OriginalCache.TouchForEviction(path);
        return Task.FromResult<FileStream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true));
    }

    public async Task EnsureAsync(string sha256, long expectedSizeBytes, Func<Stream, CancellationToken, Task> populateAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(populateAsync);
        if (await TryOpenReadAsync(sha256, expectedSizeBytes, cancellationToken) is { } existing)
        {
            await existing.DisposeAsync();
            return;
        }

        var gate = _locks.GetOrAdd(sha256, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await TryOpenReadAsync(sha256, expectedSizeBytes, cancellationToken) is { } cached)
            {
                await cached.DisposeAsync();
                return;
            }

            var path = PathFor(sha256);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = $"{path}.{Guid.CreateVersion7():N}.tmp";
            try
            {
                await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    await populateAsync(destination, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                    if (destination.Length != expectedSizeBytes)
                    {
                        throw new IOException($"The cached original for {sha256} had {destination.Length} bytes; expected {expectedSizeBytes}.");
                    }
                }
                File.Move(temporaryPath, path, overwrite: true);
                OriginalCache.TouchForEviction(path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
        }

        await EvictAsync(sha256, cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The population session takes ownership of the temporary FileStream; the finally block disposes it if ownership is not transferred.")]
    public async Task<IOriginalCachePopulation?> TryBeginPopulationAsync(
        string sha256,
        long expectedSizeBytes,
        OriginalCacheAdmission admission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSha256(sha256);
        if (expectedSizeBytes <= 0
            || expectedSizeBytes > _maxBytes
            || (admission == OriginalCacheAdmission.Automatic && expectedSizeBytes > _automaticMaxFileBytes))
        {
            return null;
        }

        var gate = _locks.GetOrAdd(sha256, static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0, CancellationToken.None)) return null;
        try
        {
            if (Exists(sha256, expectedSizeBytes))
            {
                gate.Release();
                return null;
            }

            var path = PathFor(sha256);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = $"{path}.{Guid.CreateVersion7():N}.tmp";
            FileStream? stream = null;
            try
            {
                stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                var population = new OriginalCachePopulation(
                    this,
                    gate,
                    sha256,
                    expectedSizeBytes,
                    path,
                    temporaryPath,
                    stream);
                stream = null;
                return population;
            }
            finally
            {
                if (stream is not null) await stream.DisposeAsync();
            }
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public Task EnsureFromFileAsync(string sha256, string sourcePath, long expectedSizeBytes, CancellationToken cancellationToken) =>
        EnsureAsync(sha256, expectedSizeBytes, async (destination, token) =>
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            await source.CopyToAsync(destination, token);
        }, cancellationToken);

    public async Task RemoveAsync(string sha256, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(sha256, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(sha256);
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(_rootDirectory))
            {
                foreach (var temporaryPath in Directory.EnumerateFiles(_rootDirectory, $"{sha256}.*.tmp"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<OriginalCacheEvictionResult> EvictAsync(CancellationToken cancellationToken) => EvictAsync(excludeSha256: null, cancellationToken);

    public Task<OriginalCacheStatus> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = Directory.Exists(_rootDirectory)
            ? new DirectoryInfo(_rootDirectory).EnumerateFiles()
                .Where(file => !file.Name.EndsWith(".tmp", StringComparison.Ordinal))
                .ToList()
            : [];
        var root = Path.GetPathRoot(_rootDirectory) ?? _rootDirectory;
        var availableDiskBytes = new DriveInfo(root).AvailableFreeSpace;
        return Task.FromResult(new OriginalCacheStatus(entries.Sum(file => file.Length), entries.Count, _maxBytes, availableDiskBytes));
    }

    private Task<OriginalCacheEvictionResult> EvictAsync(string? excludeSha256, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_rootDirectory)) return Task.FromResult(new OriginalCacheEvictionResult(0, 0, _maxBytes, 0, 0));

        var entries = new DirectoryInfo(_rootDirectory).EnumerateFiles()
            .Where(file => !file.Name.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderBy(file => file.LastAccessTimeUtc)
            .ThenBy(file => file.LastWriteTimeUtc)
            .ToList();
        var totalBytes = entries.Sum(file => file.Length);
        var removedEntries = 0;
        long removedBytes = 0;
        foreach (var entry in entries)
        {
            if (totalBytes <= _maxBytes) break;
            if (string.Equals(entry.Name, excludeSha256, StringComparison.Ordinal)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                entry.Delete();
                totalBytes -= entry.Length;
                removedBytes += entry.Length;
                removedEntries++;
            }
            catch (IOException)
            {
                // An active read keeps the cache entry useful; try it again on the next maintenance pass.
            }
        }
        return Task.FromResult(new OriginalCacheEvictionResult(totalBytes, entries.Count(entry => entry.Exists), _maxBytes, removedEntries, removedBytes));
    }

    private static long ParsePositiveLong(string? value, long fallback) =>
        long.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static void ValidateSha256(string sha256)
    {
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A SHA-256 cache key is required.", nameof(sha256));
        }
    }

    private sealed class OriginalCachePopulation(
        OriginalCache owner,
        SemaphoreSlim gate,
        string sha256,
        long expectedSizeBytes,
        string finalPath,
        string temporaryPath,
        FileStream stream) : IOriginalCachePopulation
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _completed;
        private bool _disposed;

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed) throw new InvalidOperationException("The cache population has already completed.");
            await stream.WriteAsync(buffer, cancellationToken);
            _hash.AppendData(buffer.Span);
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed) return;
            await stream.FlushAsync(cancellationToken);
            if (stream.Length != expectedSizeBytes)
            {
                throw new IOException($"The cached original had {stream.Length} bytes; expected {expectedSizeBytes}.");
            }

            var actualHash = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actualHash, sha256, StringComparison.Ordinal))
            {
                throw new IOException("The cached original did not match its SHA-256 identity.");
            }

            await stream.DisposeAsync();
            File.Move(temporaryPath, finalPath, overwrite: true);
            OriginalCache.TouchForEviction(finalPath);
            _completed = true;
            DisposeResources();
            await owner.EvictAsync(sha256, cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            DisposeResources();
        }

        private void DisposeResources()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                stream.Dispose();
            }
            catch (IOException)
            {
                // The cache is optional and must not break the original delivery.
            }
            finally
            {
                _hash.Dispose();
                if (!_completed)
                {
                    try
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }
                    catch (IOException)
                    {
                        // A stale .tmp file is never treated as a cache hit.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // A stale .tmp file is never treated as a cache hit.
                    }
                }
                gate.Release();
            }
        }
    }
}
