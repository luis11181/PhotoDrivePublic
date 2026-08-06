using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

internal sealed record RemoteCatalogBackupVerification(
    string BackupSetId,
    int ArtifactCount,
    long UploadedBytes,
    bool Verified);

internal sealed record CatalogBackupArtifact(string LocalPath, string ObjectKey, string ContentType);

internal interface ICatalogBackupRemoteStore
{
    Task<RemoteCatalogBackupVerification> UploadAndVerifyAsync(CatalogBackupVerification backup, UsersBackupVerification usersBackup, CancellationToken cancellationToken);
}

/// <summary>Copies a locally restore-verified catalog-and-Users backup set to its dedicated private S3 backup bucket and verifies every stored checksum.</summary>
internal sealed class S3CatalogBackupStore : ICatalogBackupRemoteStore, IDisposable
{
    private const long MaximumPutObjectBytes = 5L * 1024 * 1024 * 1024;
    private readonly AmazonS3Client _s3;
    private readonly string _bucketName;
    private readonly int _remoteRetentionDays;

    public S3CatalogBackupStore(IConfiguration configuration)
    {
        var section = configuration.GetSection("MediaStorage:S3Backups");
        _bucketName = section["BucketName"]?.Trim() ?? string.Empty;
        var profileName = section["ProfileName"]?.Trim() ?? "photodrive";
        var regionName = section["Region"]?.Trim() ?? "us-east-1";
        var originalsBucketName = configuration["MediaStorage:S3:BucketName"]?.Trim();
        _remoteRetentionDays = ParsePositiveInt(configuration["MediaStorage:Backups:RemoteRetentionDays"], 90);

        if (string.IsNullOrWhiteSpace(_bucketName))
        {
            throw new InvalidOperationException("MediaStorage:S3Backups:BucketName must identify a dedicated private backup bucket.");
        }
        if (string.Equals(_bucketName, originalsBucketName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MediaStorage:S3Backups:BucketName must differ from the originals bucket.");
        }

        var credentialChain = new CredentialProfileStoreChain();
        if (!credentialChain.TryGetAWSCredentials(profileName, out var credentials))
        {
            throw new InvalidOperationException($"AWS backup profile '{profileName}' was not found in the local shared credentials file.");
        }

        _s3 = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(regionName));
    }

    public async Task<RemoteCatalogBackupVerification> UploadAndVerifyAsync(CatalogBackupVerification backup, UsersBackupVerification usersBackup, CancellationToken cancellationToken)
    {
        var timestamp = CatalogBackupTimestamp(backup.SnapshotPath);
        if (!string.Equals(timestamp, UsersBackupTimestamp(usersBackup.SnapshotPath), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Catalog and Users snapshots must belong to the same backup set before remote upload.");
        }
        var artifacts = BuildArtifacts(backup, usersBackup, timestamp);

        var versioning = await _s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = _bucketName }, cancellationToken);
        if (versioning.VersioningConfig?.Status != VersionStatus.Enabled)
        {
            throw new InvalidOperationException("The dedicated S3 backup bucket must have versioning enabled before catalog backups can be uploaded.");
        }
        await VerifyRetentionPolicyAsync(cancellationToken);

        long uploadedBytes = 0;
        foreach (var artifact in artifacts)
        {
            uploadedBytes += await UploadAndVerifyArtifactAsync(artifact, cancellationToken);
        }

        return new RemoteCatalogBackupVerification(timestamp, artifacts.Count, uploadedBytes, Verified: true);
    }

    internal static string CatalogBackupTimestamp(string snapshotPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(snapshotPath);
        if (!fileName.StartsWith("catalog-", StringComparison.Ordinal) || fileName.Length == "catalog-".Length)
        {
            throw new ArgumentException("The snapshot filename must use the catalog-<UTC timestamp>.db convention.", nameof(snapshotPath));
        }

        return fileName["catalog-".Length..];
    }

    internal static string UsersBackupTimestamp(string snapshotPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(snapshotPath);
        if (!fileName.StartsWith("users-", StringComparison.Ordinal) || fileName.Length == "users-".Length)
        {
            throw new ArgumentException("The snapshot filename must use the users-<UTC timestamp>.db convention.", nameof(snapshotPath));
        }

        return fileName["users-".Length..];
    }

    internal static IReadOnlyList<CatalogBackupArtifact> BuildArtifacts(
        CatalogBackupVerification backup,
        UsersBackupVerification usersBackup,
        string timestamp)
    {
        var artifacts = new List<CatalogBackupArtifact>
        {
            new(backup.SnapshotPath, $"backups/sqlite/{timestamp}/catalog.db", "application/vnd.sqlite3"),
            new(usersBackup.SnapshotPath, $"backups/sqlite/{timestamp}/users.db", "application/vnd.sqlite3")
        };

        if (backup.CatalogExportPath is null && usersBackup.UsersExportPath is null) return artifacts;
        if (backup.CatalogExportPath is null || usersBackup.UsersExportPath is null)
        {
            throw new InvalidOperationException("Catalog and Users portable exports must either both be present or both be disabled.");
        }

        var exportDirectory = Path.GetDirectoryName(backup.CatalogExportPath)
            ?? throw new InvalidOperationException("The catalog export directory could not be resolved.");
        var usersExportDirectory = Path.GetDirectoryName(usersBackup.UsersExportPath)
            ?? throw new InvalidOperationException("The Users export directory could not be resolved.");
        artifacts.AddRange(
        [
            new(backup.CatalogExportPath, $"catalog-exports/{timestamp}/catalog-v1.jsonl", "application/x-ndjson"),
            new(Path.Combine(exportDirectory, "catalog-v1.sha256"), $"catalog-exports/{timestamp}/catalog-v1.sha256", "text/plain"),
            new(Path.Combine(exportDirectory, "RECOVERY-README.md"), $"catalog-exports/{timestamp}/RECOVERY-README.md", "text/markdown"),
            new(usersBackup.UsersExportPath, $"users-exports/{timestamp}/users-v1.jsonl", "application/x-ndjson"),
            new(Path.Combine(usersExportDirectory, "users-v1.sha256"), $"users-exports/{timestamp}/users-v1.sha256", "text/plain"),
            new(Path.Combine(usersExportDirectory, "RECOVERY-README.md"), $"users-exports/{timestamp}/RECOVERY-README.md", "text/markdown")
        ]);
        return artifacts;
    }

    private async Task<long> UploadAndVerifyArtifactAsync(CatalogBackupArtifact artifact, CancellationToken cancellationToken)
    {
        var file = new FileInfo(artifact.LocalPath);
        if (!file.Exists) throw new FileNotFoundException("A verified catalog backup artifact is missing.", artifact.LocalPath);
        if (file.Length > MaximumPutObjectBytes)
        {
            throw new InvalidOperationException($"Backup artifact '{file.Name}' exceeds S3's 5 GiB PutObject limit. The verified local backup was retained, but remote backup multipart support is required before this artifact can be copied.");
        }

        string checksum;
        await using (var hashStream = file.OpenRead())
        {
            checksum = Convert.ToBase64String(await SHA256.HashDataAsync(hashStream, cancellationToken));
        }

        await using (var uploadStream = file.OpenRead())
        {
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = artifact.ObjectKey,
                InputStream = uploadStream,
                AutoCloseStream = false,
                ContentType = artifact.ContentType,
                ChecksumSHA256 = checksum
            }, cancellationToken);
        }

        var attributes = await _s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = _bucketName,
            Key = artifact.ObjectKey,
            ObjectAttributes = [ObjectAttributes.Checksum, ObjectAttributes.ObjectSize]
        }, cancellationToken);
        if (attributes.ObjectSize != file.Length || !string.Equals(attributes.Checksum?.ChecksumSHA256, checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Remote verification failed for backup artifact '{file.Name}'.");
        }

        return file.Length;
    }

    public void Dispose() => _s3.Dispose();

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
            throw new InvalidOperationException($"The dedicated S3 backup bucket must enable lifecycle rule 'MediaExplorerCatalogBackupRetention' with a {_remoteRetentionDays}-day expiration before catalog backups can be uploaded.");
        }
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

}
