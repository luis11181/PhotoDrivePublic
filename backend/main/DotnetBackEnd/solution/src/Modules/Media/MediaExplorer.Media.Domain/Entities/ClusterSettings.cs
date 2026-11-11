namespace MediaExplorer.Media.Domain;

/// <summary>
/// The single shared row (Id = 1) that coordinates hosts: which host runs installation-wide background work
/// (metadata extraction and database backups), and the outcome of the latest scheduled database backup.
/// </summary>
public sealed class ClusterSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public Guid? PrimaryHostId { get; set; }
    public DateTime? PrimaryChangedAt { get; set; }
    public Guid? PrimaryChangedByUserId { get; set; }
    public DateTime? LastBackupVerifiedAt { get; set; }
    public string? LastBackupSetId { get; set; }
    public DateTime? LastBackupFailedAt { get; set; }
    public string? LastBackupFailureCode { get; set; }
    public int BackupConsecutiveFailures { get; set; }
}
