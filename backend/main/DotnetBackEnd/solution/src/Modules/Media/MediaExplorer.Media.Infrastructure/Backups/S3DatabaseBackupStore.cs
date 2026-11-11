using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

internal sealed record RemoteDatabaseBackupVerification(string BackupSetId, int ArtifactCount, long UploadedBytes);

internal interface IDatabaseBackupRemoteStore
{
    Task<RemoteDatabaseBackupVerification> UploadAndVerifyAsync(DatabaseBackupVerification backup, CancellationToken cancellationToken);
}

/// <summary>
/// Copies a verified database snapshot and its manifest to <c>turso-backups/&lt;set&gt;/</c> in the dedicated private
/// backup bucket and verifies every stored checksum. The manifest is written last, so a set with a manifest is
/// complete. Old sets are removed by the bucket's age-based lifecycle rule, which this store requires.
/// </summary>
internal sealed class S3DatabaseBackupStore : IDatabaseBackupRemoteStore, IDisposable
{
    public const string Prefix = "turso-backups/";
    private const long MaximumPutObjectBytes = 5L * 1024 * 1024 * 1024;
    private readonly AmazonS3Client _s3;
    private readonly string _bucketName;
    private readonly int _remoteRetentionDays;

    public S3DatabaseBackupStore(IConfiguration configuration)
    {
        var section = configuration.GetSection("MediaStorage:S3Backups");
        _bucketName = section["BucketName"]?.Trim() ?? string.Empty;
        var profileName = section["ProfileName"]?.Trim() ?? "photodrive";
        var regionName = section["Region"]?.Trim() ?? "us-east-1";
        _remoteRetentionDays = configuration.GetValue("MediaStorage:Backups:RemoteRetentionDays", 90);

        if (string.IsNullOrWhiteSpace(_bucketName))
            throw new InvalidOperationException("MediaStorage:S3Backups:BucketName must identify a dedicated private backup bucket.");
        if (string.Equals(_bucketName, configuration["MediaStorage:S3:BucketName"]?.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("MediaStorage:S3Backups:BucketName must differ from the originals bucket.");

        var credentialChain = new CredentialProfileStoreChain();
        if (!credentialChain.TryGetAWSCredentials(profileName, out var credentials))
            throw new InvalidOperationException($"AWS backup profile '{profileName}' was not found in the local shared credentials file.");

        _s3 = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(regionName));
    }

    public async Task<RemoteDatabaseBackupVerification> UploadAndVerifyAsync(DatabaseBackupVerification backup, CancellationToken cancellationToken)
    {
        var versioning = await _s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = _bucketName }, cancellationToken);
        if (versioning.VersioningConfig?.Status != VersionStatus.Enabled)
            throw new InvalidOperationException("The dedicated S3 backup bucket must have versioning enabled before database backups can be uploaded.");
        await VerifyRetentionPolicyAsync(cancellationToken);

        var prefix = $"{Prefix}{backup.BackupSetId}/";
        var uploaded = await UploadAndVerifyArtifactAsync(backup.SnapshotPath, prefix + "photodrive.db", "application/vnd.sqlite3", cancellationToken);
        uploaded += await UploadAndVerifyArtifactAsync(backup.ManifestPath, prefix + "manifest.json", "application/json", cancellationToken);
        return new RemoteDatabaseBackupVerification(backup.BackupSetId, 2, uploaded);
    }

    private async Task<long> UploadAndVerifyArtifactAsync(string localPath, string key, string contentType, CancellationToken cancellationToken)
    {
        var file = new FileInfo(localPath);
        if (!file.Exists) throw new FileNotFoundException("A verified backup artifact is missing.", localPath);
        if (file.Length > MaximumPutObjectBytes)
            throw new InvalidOperationException($"Backup artifact '{file.Name}' exceeds S3's 5 GiB PutObject limit; multipart upload support is required.");

        string checksum;
        await using (var hashStream = file.OpenRead())
            checksum = Convert.ToBase64String(await SHA256.HashDataAsync(hashStream, cancellationToken));

        await using (var uploadStream = file.OpenRead())
        {
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = uploadStream,
                AutoCloseStream = false,
                ContentType = contentType,
                ChecksumSHA256 = checksum
            }, cancellationToken);
        }

        var attributes = await _s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = _bucketName,
            Key = key,
            ObjectAttributes = [ObjectAttributes.Checksum, ObjectAttributes.ObjectSize]
        }, cancellationToken);
        if (attributes.ObjectSize != file.Length || !string.Equals(attributes.Checksum?.ChecksumSHA256, checksum, StringComparison.Ordinal))
            throw new InvalidOperationException($"Remote verification failed for backup artifact '{file.Name}'.");

        return file.Length;
    }

    private async Task VerifyRetentionPolicyAsync(CancellationToken cancellationToken)
    {
        LifecycleConfiguration? configuration = null;
        try
        {
            var response = await _s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = _bucketName }, cancellationToken);
            configuration = response.Configuration;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound || exception.ErrorCode == "NoSuchLifecycleConfiguration")
        {
            // The actionable configuration error below is clearer than the provider-specific 404.
        }

        var retentionRule = configuration?.Rules?.FirstOrDefault(rule =>
            string.Equals(rule.Id, "MediaExplorerCatalogBackupRetention", StringComparison.Ordinal) &&
            rule.Status == LifecycleRuleStatus.Enabled);
        if (retentionRule?.Expiration?.Days != _remoteRetentionDays)
        {
            throw new InvalidOperationException(
                $"The dedicated S3 backup bucket must enable lifecycle rule 'MediaExplorerCatalogBackupRetention' with a {_remoteRetentionDays}-day expiration before database backups can be uploaded.");
        }
    }

    public void Dispose() => _s3.Dispose();
}
