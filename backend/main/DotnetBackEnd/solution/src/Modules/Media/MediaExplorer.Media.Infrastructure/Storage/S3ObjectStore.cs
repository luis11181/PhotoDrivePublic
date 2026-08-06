using System.Security.Cryptography;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal interface IObjectStore : IDisposable
{
    Task PutOriginalIfMissingAsync(string sha256, Stream content, long sizeBytes, string mimeType, string originalName, Guid uploaderId, string uploaderEmail, DateTime uploadedAt, CancellationToken cancellationToken);
    Task PutRecoveryManifestIfMissingAsync(RecoveryManifestData manifest, CancellationToken cancellationToken);
    Task<ObjectReadResult> OpenOriginalAsync(string sha256, long? rangeStart, long? rangeEnd, CancellationToken cancellationToken);
    Task<bool> OriginalExistsAsync(string sha256, CancellationToken cancellationToken);
    Task DeleteOriginalPermanentlyAsync(string sha256, CancellationToken cancellationToken);
    Task<StorageInventory> ListOriginalsForReconciliationAsync(CancellationToken cancellationToken);
}

/// <summary>Private storage inventory used only by explicitly invoked developer reconciliation.</summary>
internal sealed record StorageOriginalDescriptor(string Sha256, long VersionBytes);
internal sealed record StorageInventorySummary(
    int OriginalKeyCount,
    int OriginalVersionCount,
    long OriginalVersionBytes,
    int RecoveryManifestKeyCount,
    int RecoveryManifestVersionCount,
    long RecoveryManifestVersionBytes,
    int DeleteMarkerCount);
internal sealed record StorageInventory(IReadOnlyList<StorageOriginalDescriptor> Originals, StorageInventorySummary Summary);

internal sealed record RecoveryManifestData(
    string Sha256,
    long SizeBytes,
    string OriginalFilename,
    string DeclaredMimeType,
    string DetectedMimeType,
    string? MediaFormat,
    Guid UploaderId,
    string UploaderEmail,
    DateTime UploadedAt,
    DateTime EffectiveCapturedAt,
    string CaptureDateSource,
    DateTime? EmbeddedCapturedAt,
    int? Width,
    int? Height,
    int? Orientation);

internal sealed class ObjectReadResult : IDisposable
{
    private readonly IDisposable? _lifetime;

    public ObjectReadResult(GetObjectResponse response)
    {
        Content = response.ResponseStream;
        ContentType = response.Headers.ContentType ?? "application/octet-stream";
        ContentLength = response.ContentLength;
        LastModified = response.LastModified?.ToUniversalTime() ?? DateTime.UtcNow;
        _lifetime = response;
    }

    internal ObjectReadResult(Stream content, string contentType, long contentLength, DateTime lastModified, IDisposable? lifetime = null)
    {
        Content = content;
        ContentType = contentType;
        ContentLength = contentLength;
        LastModified = lastModified;
        _lifetime = lifetime;
    }

    public Stream Content { get; }
    public string ContentType { get; }
    public long ContentLength { get; }
    public DateTime LastModified { get; }

    public void Dispose()
    {
        Content.Dispose();
        _lifetime?.Dispose();
    }
}

internal interface IObjectStoreFactory
{
    IObjectStore Create();
}

internal sealed class S3ObjectStoreFactory(IConfiguration configuration, ILoggerFactory loggerFactory) : IObjectStoreFactory
{
    public IObjectStore Create() => new S3ObjectStore(configuration, loggerFactory.CreateLogger<S3ObjectStore>());
}

/// <summary>Private S3 storage for immutable, content-addressed original bytes.</summary>
internal sealed class S3ObjectStore : IObjectStore, IDisposable
{
    internal const long DirectPutObjectLimitBytes = 5L * 1024 * 1024 * 1024;
    private const long MultipartTargetPartSizeBytes = 64L * 1024 * 1024;
    private const int MultipartMaximumPartCount = 10_000;
    private readonly AmazonS3Client _s3;
    private readonly string _bucketName;
    private readonly ILogger<S3ObjectStore> _logger;

    public S3ObjectStore(IConfiguration configuration, ILogger<S3ObjectStore> logger)
    {
        _logger = logger;
        var section = configuration.GetSection("MediaStorage:S3");
        _bucketName = section["BucketName"]?.Trim() ?? string.Empty;
        var profileName = section["ProfileName"]?.Trim() ?? "photodrive";
        var regionName = section["Region"]?.Trim() ?? "us-east-1";

        if (string.IsNullOrWhiteSpace(_bucketName))
        {
            throw new InvalidOperationException("MediaStorage:S3:BucketName must be set in an untracked local configuration file before uploads can run.");
        }

        var credentialChain = new CredentialProfileStoreChain();
        if (!credentialChain.TryGetAWSCredentials(profileName, out var credentials))
        {
            throw new InvalidOperationException($"AWS profile '{profileName}' was not found in the local shared credentials file.");
        }

        _s3 = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(regionName));
    }

    public async Task PutOriginalIfMissingAsync(string sha256, Stream content, long sizeBytes, string mimeType, string originalName, Guid uploaderId, string uploaderEmail, DateTime uploadedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploaderEmail);
        var objectKey = BlobKey(sha256);
        if (!await ObjectExistsAsync(objectKey, cancellationToken))
        {
            content.Position = 0;
            try
            {
                if (sizeBytes <= DirectPutObjectLimitBytes)
                {
                    _logger.LogInformation("[media.s3] PUT original started mode=single sizeBytes={SizeBytes}", sizeBytes);
                    await PutOriginalAsync(objectKey, sha256, content, mimeType, originalName, uploaderId, uploaderEmail, uploadedAt, cancellationToken);
                    _logger.LogInformation("[media.s3] PUT original completed mode=single sizeBytes={SizeBytes}", sizeBytes);
                }
                else
                {
                    _logger.LogInformation("[media.s3] PUT original started mode=multipart sizeBytes={SizeBytes}", sizeBytes);
                    await PutOriginalMultipartAsync(objectKey, sha256, content, sizeBytes, mimeType, originalName, uploaderId, uploaderEmail, uploadedAt, cancellationToken);
                    _logger.LogInformation("[media.s3] PUT original completed mode=multipart sizeBytes={SizeBytes}", sizeBytes);
                }
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                _logger.LogInformation("[media.s3] PUT original skipped reason=concurrent_deduplication");
                // A concurrent completion created the same content-addressed immutable blob.
            }
        }
        else
        {
            _logger.LogInformation("[media.s3] PUT original skipped reason=already_exists");
        }

    }

    public async Task<ObjectReadResult> OpenOriginalAsync(string sha256, long? rangeStart, long? rangeEnd, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[media.s3] GET original started rangeStart={RangeStart} rangeEnd={RangeEnd}",
            rangeStart,
            rangeEnd);
        var request = new GetObjectRequest { BucketName = _bucketName, Key = BlobKey(sha256) };
        if (rangeStart.HasValue)
        {
            request.ByteRange = rangeEnd.HasValue
                ? new ByteRange(rangeStart.Value, rangeEnd.Value)
                : new ByteRange($"bytes={rangeStart.Value}-");
        }
        var response = await _s3.GetObjectAsync(request, cancellationToken);
        _logger.LogInformation(
            "[media.s3] GET original completed contentLength={ContentLength} rangeStart={RangeStart} rangeEnd={RangeEnd}",
            response.ContentLength,
            rangeStart,
            rangeEnd);
        return new ObjectReadResult(response);
    }

    public Task<bool> OriginalExistsAsync(string sha256, CancellationToken cancellationToken) =>
        ObjectExistsAsync(BlobKey(sha256), cancellationToken);

    public async Task DeleteOriginalPermanentlyAsync(string sha256, CancellationToken cancellationToken)
    {
        _logger.LogWarning("[media.s3] permanent original deletion started");
        await DeleteEveryVersionAsync(BlobKey(sha256), "original", cancellationToken);
        await DeleteEveryVersionAsync(RecoveryManifestKey(sha256), "recovery_manifest", cancellationToken);
        if (await ExactKeyHasVersionsAsync(BlobKey(sha256), cancellationToken)
            || await ExactKeyHasVersionsAsync(RecoveryManifestKey(sha256), cancellationToken))
            throw new AmazonS3Exception("Permanent deletion verification found remaining object versions.");
        _logger.LogWarning("[media.s3] permanent original deletion completed");
    }

    private async Task<bool> ExactKeyHasVersionsAsync(string exactKey, CancellationToken cancellationToken)
    {
        string? keyMarker = null;
        string? versionIdMarker = null;
        do
        {
            var response = await _s3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = _bucketName,
                Prefix = exactKey,
                KeyMarker = keyMarker,
                VersionIdMarker = versionIdMarker,
                MaxKeys = 1_000
            }, cancellationToken);
            if ((response.Versions ?? []).Any(version => string.Equals(version.Key, exactKey, StringComparison.Ordinal)))
                return true;
            if (response.IsTruncated != true) return false;
            keyMarker = response.NextKeyMarker;
            versionIdMarker = response.NextVersionIdMarker;
        } while (true);
    }

    public async Task<StorageInventory> ListOriginalsForReconciliationAsync(CancellationToken cancellationToken)
    {
        var versionBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        var summary = new StorageInventorySummaryBuilder();
        await InventoryPrefixAsync("blobs/sha256/", isOriginal: true, versionBytes, summary, cancellationToken);
        await InventoryPrefixAsync("recovery-manifests/", isOriginal: false, versionBytes, summary, cancellationToken);
        var originals = versionBytes
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new StorageOriginalDescriptor(entry.Key, entry.Value))
            .ToArray();
        _logger.LogInformation("[media.s3] developer reconciliation inventory completed trackedKeys={StorageKeyCount}", originals.Length);
        return new StorageInventory(originals, summary.ToResult());
    }

    private async Task InventoryPrefixAsync(
        string prefix,
        bool isOriginal,
        Dictionary<string, long> versionBytes,
        StorageInventorySummaryBuilder summary,
        CancellationToken cancellationToken)
    {
        string? keyMarker = null;
        string? versionIdMarker = null;
        bool isTruncated;
        do
        {
            var response = await _s3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = _bucketName,
                Prefix = prefix,
                KeyMarker = keyMarker,
                VersionIdMarker = versionIdMarker,
                MaxKeys = 1000
            }, cancellationToken);
            foreach (var version in response.Versions ?? [])
            {
                var sha256 = Sha256ForInventoryKey(version.Key);
                if (sha256 is null) continue;
                versionBytes.TryAdd(sha256, 0);
                if (version.IsDeleteMarker == true)
                {
                    summary.AddDeleteMarker(isOriginal, sha256);
                    continue;
                }
                var sizeBytes = version.Size ?? 0;
                versionBytes[sha256] += sizeBytes;
                summary.AddVersion(isOriginal, sha256, sizeBytes);
            }
            isTruncated = response.IsTruncated == true;
            keyMarker = isTruncated ? response.NextKeyMarker : null;
            versionIdMarker = isTruncated ? response.NextVersionIdMarker : null;
        }
        while (isTruncated);
    }

    private static string? Sha256ForInventoryKey(string key)
    {
        const string blobPrefix = "blobs/sha256/";
        const string manifestPrefix = "recovery-manifests/";
        string? candidate = null;
        if (key.StartsWith(blobPrefix, StringComparison.Ordinal)) candidate = key[blobPrefix.Length..];
        else if (key.StartsWith(manifestPrefix, StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal)) candidate = key[manifestPrefix.Length..^".json".Length];
        return candidate is { Length: 64 } && candidate.All(Uri.IsHexDigit) ? candidate.ToLowerInvariant() : null;
    }

    private sealed class StorageInventorySummaryBuilder
    {
        private readonly HashSet<string> _originalKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _recoveryManifestKeys = new(StringComparer.Ordinal);
        private int _originalVersionCount;
        private long _originalVersionBytes;
        private int _recoveryManifestVersionCount;
        private long _recoveryManifestVersionBytes;
        private int _deleteMarkerCount;

        public void AddVersion(bool isOriginal, string sha256, long sizeBytes)
        {
            (isOriginal ? _originalKeys : _recoveryManifestKeys).Add(sha256);
            if (isOriginal)
            {
                _originalVersionCount++;
                _originalVersionBytes += sizeBytes;
                return;
            }
            _recoveryManifestVersionCount++;
            _recoveryManifestVersionBytes += sizeBytes;
        }

        public void AddDeleteMarker(bool isOriginal, string sha256)
        {
            (isOriginal ? _originalKeys : _recoveryManifestKeys).Add(sha256);
            _deleteMarkerCount++;
        }

        public StorageInventorySummary ToResult() => new(
            _originalKeys.Count,
            _originalVersionCount,
            _originalVersionBytes,
            _recoveryManifestKeys.Count,
            _recoveryManifestVersionCount,
            _recoveryManifestVersionBytes,
            _deleteMarkerCount);
    }

    private async Task DeleteEveryVersionAsync(string exactKey, string objectKind, CancellationToken cancellationToken)
    {
        string? keyMarker = null;
        string? versionIdMarker = null;
        var versions = new List<KeyVersion>();
        bool isTruncated;
        do
        {
            var response = await _s3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = _bucketName,
                Prefix = exactKey,
                KeyMarker = keyMarker,
                VersionIdMarker = versionIdMarker,
                MaxKeys = 1000
            }, cancellationToken);
            versions.AddRange((response.Versions ?? [])
                .Where(version => string.Equals(version.Key, exactKey, StringComparison.Ordinal))
                .Select(version => new KeyVersion { Key = exactKey, VersionId = version.VersionId }));
            isTruncated = response.IsTruncated == true;
            keyMarker = isTruncated ? response.NextKeyMarker : null;
            versionIdMarker = isTruncated ? response.NextVersionIdMarker : null;
        }
        while (isTruncated);

        foreach (var batch in versions.Chunk(1000))
        {
            var deleted = await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = _bucketName,
                Objects = batch.ToList(),
                Quiet = true
            }, cancellationToken);
            if (deleted.DeleteErrors is { Count: > 0 } deleteErrors)
            {
                throw new AmazonS3Exception($"S3 rejected {deleteErrors.Count} permanent {objectKind} version deletions.");
            }
            _logger.LogWarning(
                "[media.s3] permanent object versions deleted objectKind={ObjectKind} versionCount={VersionCount}",
                objectKind,
                batch.Length);
        }
    }

    private Task<PutObjectResponse> PutOriginalAsync(string objectKey, string sha256, Stream content, string mimeType, string originalName, Guid uploaderId, string uploaderEmail, DateTime uploadedAt, CancellationToken cancellationToken)
    {
        content.Position = 0;
        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = objectKey,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = mimeType,
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            IfNoneMatch = "*"
        };
        AddOriginalMetadata(request.Metadata, sha256, originalName, uploaderId, uploaderEmail, uploadedAt);
        return _s3.PutObjectAsync(request, cancellationToken);
    }

    private async Task PutOriginalMultipartAsync(string objectKey, string sha256, Stream content, long sizeBytes, string mimeType, string originalName, Guid uploaderId, string uploaderEmail, DateTime uploadedAt, CancellationToken cancellationToken)
    {
        if (!content.CanSeek) throw new ArgumentException("Multipart uploads require a seekable staged stream.", nameof(content));

        var partSize = MultipartPartSize(sizeBytes);
        content.Position = 0;
        var initiateRequest = new InitiateMultipartUploadRequest
        {
            BucketName = _bucketName,
            Key = objectKey,
            ContentType = mimeType
        };
        AddOriginalMetadata(initiateRequest.Metadata, sha256, originalName, uploaderId, uploaderEmail, uploadedAt);
        var started = await _s3.InitiateMultipartUploadAsync(initiateRequest, cancellationToken);
        _logger.LogInformation("[media.s3] multipart session created partSizeBytes={PartSizeBytes}", partSize);

        try
        {
            var partETags = new List<PartETag>();
            long position = 0;
            var partNumber = 1;
            while (position < sizeBytes)
            {
                var length = Math.Min(partSize, sizeBytes - position);
                var response = await _s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey,
                    UploadId = started.UploadId,
                    PartNumber = partNumber,
                    InputStream = content,
                    PartSize = length,
                    IsLastPart = position + length == sizeBytes
                }, cancellationToken);
                _logger.LogInformation(
                    "[media.s3] multipart part uploaded partNumber={PartNumber} sizeBytes={SizeBytes}",
                    partNumber,
                    length);
                partETags.Add(new PartETag(partNumber, response.ETag));
                position += length;
                partNumber++;
            }

            await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucketName,
                Key = objectKey,
                UploadId = started.UploadId,
                PartETags = partETags,
                MpuObjectSize = sizeBytes,
                IfNoneMatch = "*"
            }, cancellationToken);
            _logger.LogInformation("[media.s3] multipart session completed parts={PartCount}", partETags.Count);
        }
        catch
        {
            try
            {
                await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey,
                    UploadId = started.UploadId
                }, CancellationToken.None);
                _logger.LogWarning("[media.s3] multipart session aborted after upload failure");
            }
            catch (AmazonS3Exception)
            {
                // The original failure is more actionable; S3 lifecycle cleanup remains a fallback.
            }
            throw;
        }
    }

    private static void AddOriginalMetadata(MetadataCollection metadata, string sha256, string originalName, Guid uploaderId, string uploaderEmail, DateTime uploadedAt)
    {
        metadata["sha256"] = sha256;
        metadata["original-upload-filename"] = originalName;
        metadata["uploaded-at"] = uploadedAt.ToUniversalTime().ToString("O");
        metadata["uploader-id"] = uploaderId.ToString();
        metadata["uploader-email"] = uploaderEmail.Trim();
        metadata["metadata-schema-version"] = "2";
    }

    internal static long MultipartPartSize(long sizeBytes)
    {
        var minimumForMaximumPartCount = (long)Math.Ceiling(sizeBytes / (double)MultipartMaximumPartCount);
        return Math.Max(MultipartTargetPartSizeBytes, minimumForMaximumPartCount);
    }

    public async Task PutRecoveryManifestIfMissingAsync(RecoveryManifestData manifestData, CancellationToken cancellationToken)
    {
        var manifestKey = RecoveryManifestKey(manifestData.Sha256);
        if (await ObjectExistsAsync(manifestKey, cancellationToken))
        {
            _logger.LogInformation("[media.s3] PUT recovery manifest skipped reason=already_exists");
            return;
        }

        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            blobKey = BlobKey(manifestData.Sha256),
            sha256 = manifestData.Sha256,
            sizeBytes = manifestData.SizeBytes,
            originalFilename = manifestData.OriginalFilename,
            declaredMimeType = manifestData.DeclaredMimeType,
            detectedMimeType = manifestData.DetectedMimeType,
            mediaFormat = manifestData.MediaFormat,
            uploadedAt = manifestData.UploadedAt.ToUniversalTime(),
            uploaderId = manifestData.UploaderId,
            uploaderEmail = manifestData.UploaderEmail,
            effectiveCapturedAt = manifestData.EffectiveCapturedAt,
            captureDateSource = manifestData.CaptureDateSource,
            embeddedCapturedAt = manifestData.EmbeddedCapturedAt,
            width = manifestData.Width,
            height = manifestData.Height,
            orientation = manifestData.Orientation,
            metadataSchemaVersion = 2
        });
        await using var stream = new MemoryStream(manifest, writable: false);
        try
        {
            _logger.LogInformation("[media.s3] PUT recovery manifest started sizeBytes={SizeBytes}", manifest.Length);
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = manifestKey,
                InputStream = stream,
                AutoCloseStream = false,
                ContentType = "application/json",
                IfNoneMatch = "*"
            }, cancellationToken);
            _logger.LogInformation("[media.s3] PUT recovery manifest completed sizeBytes={SizeBytes}", manifest.Length);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            _logger.LogInformation("[media.s3] PUT recovery manifest skipped reason=concurrent_creation");
            // The first immutable recovery manifest wins.
        }
    }

    private async Task<bool> ObjectExistsAsync(string key, CancellationToken cancellationToken)
    {
        var objectKind = key.StartsWith("blobs/", StringComparison.Ordinal) ? "original" : "recovery_manifest";
        _logger.LogInformation("[media.s3] HEAD {ObjectKind} started", objectKind);
        try
        {
            await _s3.GetObjectMetadataAsync(_bucketName, key, cancellationToken);
            _logger.LogInformation("[media.s3] HEAD {ObjectKind} completed exists=true", objectKind);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound || exception.ErrorCode is "NoSuchKey" or "NotFound")
        {
            _logger.LogInformation("[media.s3] HEAD {ObjectKind} completed exists=false", objectKind);
            return false;
        }
    }

    public static string BlobKey(string sha256) => $"blobs/sha256/{sha256}";
    internal static string RecoveryManifestKey(string sha256) => $"recovery-manifests/{sha256}.json";

    public void Dispose() => _s3.Dispose();
}

internal static class UploadHash
{
    public static async Task<string> Sha256Async(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
