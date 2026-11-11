using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Infrastructure;

public sealed class MediaCatalogDbContext(DbContextOptions<MediaCatalogDbContext> options) : DbContext(options)
{
    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<CatalogFolder> Folders => Set<CatalogFolder>();
    public DbSet<CatalogFile> Files => Set<CatalogFile>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<StorageCleanupCandidate> StorageCleanupCandidates => Set<StorageCleanupCandidate>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<FileVersionMetadata> FileVersionMetadata => Set<FileVersionMetadata>();
    public DbSet<FileUserState> FileUserStates => Set<FileUserState>();
    public DbSet<FolderUserState> FolderUserStates => Set<FolderUserState>();
    public DbSet<CatalogShare> CatalogShares => Set<CatalogShare>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<AlbumItem> AlbumItems => Set<AlbumItem>();
    public DbSet<TimelineEvent> TimelineEvents => Set<TimelineEvent>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<FileTag> FileTags => Set<FileTag>();
    public DbSet<FolderTag> FolderTags => Set<FolderTag>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportItem> ImportItems => Set<ImportItem>();
    public DbSet<CacheBuildJob> CacheBuildJobs => Set<CacheBuildJob>();
    public DbSet<CacheBuildItem> CacheBuildItems => Set<CacheBuildItem>();
    public DbSet<ServerHost> Hosts => Set<ServerHost>();
    public DbSet<ClusterSettings> Cluster => Set<ClusterSettings>();

    /// <summary>Saves and refreshes the derived search rows for changed names, titles, and tags atomically.</summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var search = await CatalogSearchIndex.CollectAsync(this, cancellationToken);
        if (search.IsEmpty) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        var ownsTransaction = Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await Database.BeginTransactionAsync(cancellationToken) : null;
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await CatalogSearchIndex.ApplyAsync(this, search, cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Library>(entity =>
        {
            entity.ToTable("libraries");
            entity.HasKey(library => library.Id);
            entity.Property(library => library.Name).HasMaxLength(200).IsRequired();
            entity.Property(library => library.NextItemSequence).IsRequired();
            entity.HasIndex(library => library.OwnerUserId).IsUnique();
        });

        modelBuilder.Entity<CatalogFolder>(entity =>
        {
            entity.ToTable("folders");
            entity.HasKey(folder => folder.Id);
            entity.Property(folder => folder.Sequence);
            entity.Property(folder => folder.Name).HasMaxLength(255).IsRequired();
            entity.Property(folder => folder.SystemRole).HasMaxLength(64);
            entity.HasIndex(folder => new { folder.LibraryId, folder.TrashedAt, folder.Sequence });
            entity.HasIndex(folder => new { folder.LibraryId, folder.TrashRootId });
            entity.HasIndex(folder => new { folder.LibraryId, folder.ParentFolderId, folder.Sequence });
            entity.HasIndex(folder => new { folder.LibraryId, folder.SystemRole }).IsUnique();
            entity.HasOne<Library>().WithMany().HasForeignKey(folder => folder.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CatalogFolder>().WithMany().HasForeignKey(folder => folder.ParentFolderId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CatalogFile>(entity =>
        {
            entity.ToTable("files");
            entity.HasKey(file => file.Id);
            entity.Property(file => file.Sequence);
            entity.Property(file => file.Name).HasMaxLength(255).IsRequired();
            entity.Property(file => file.MimeType).HasMaxLength(255).IsRequired();
            entity.Property(file => file.CaptureDateSource).HasMaxLength(64);
            entity.Property(file => file.MediaFormat).HasMaxLength(64);
            entity.Property(file => file.MetadataState).HasMaxLength(32).IsRequired();
            entity.Property(file => file.RecoveryManifestState).HasMaxLength(32).IsRequired();
            entity.Property(file => file.MetadataErrorCode).HasMaxLength(128);
            entity.Property(file => file.MetadataErrorMessage).HasMaxLength(1000);
            entity.Property(file => file.Description).HasMaxLength(4000);
            entity.Property(file => file.CapturedAt).IsRequired();
            entity.Property(file => file.CurrentVersionId).IsRequired();
            entity.HasIndex(file => new { file.LibraryId, file.TrashedAt, file.Sequence });
            entity.HasIndex(file => new { file.LibraryId, file.TrashRootId });
            entity.HasIndex(file => new { file.LibraryId, file.ParentFolderId, file.Sequence });
            entity.HasIndex(file => new { file.LibraryId, file.TrashedAt, file.CapturedAt, file.Sequence });
            entity.HasOne<Library>().WithMany().HasForeignKey(file => file.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CatalogFolder>().WithMany().HasForeignKey(file => file.ParentFolderId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Blob>(entity =>
        {
            entity.ToTable("blobs");
            entity.HasKey(blob => blob.Sha256);
            entity.Property(blob => blob.Sha256).HasMaxLength(64);
            entity.Property(blob => blob.ObjectKey).HasMaxLength(512).IsRequired();
            entity.Property(blob => blob.LifecycleState).HasMaxLength(32).HasDefaultValue(Blob.AvailableLifecycleState).IsRequired();
            entity.Property(blob => blob.DeletionError).HasMaxLength(1000);
            entity.HasIndex(blob => blob.LifecycleState);
        });

        modelBuilder.Entity<FileVersion>(entity =>
        {
            entity.ToTable("file_versions");
            entity.HasKey(version => version.Id);
            entity.Property(version => version.BlobSha256).HasMaxLength(64).IsRequired();
            entity.Property(version => version.MimeType).HasMaxLength(255).IsRequired();
            entity.Property(version => version.DeclaredMimeType).HasMaxLength(255).IsRequired();
            entity.Property(version => version.OriginalName).HasMaxLength(255).IsRequired();
            entity.HasIndex(version => version.FileId);
            entity.HasIndex(version => version.BlobSha256);
            entity.HasOne<CatalogFile>().WithMany().HasForeignKey(version => version.FileId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Blob>().WithMany().HasForeignKey(version => version.BlobSha256).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StorageCleanupCandidate>(entity =>
        {
            entity.ToTable("storage_cleanup_candidates");
            entity.HasKey(candidate => candidate.Id);
            entity.Property(candidate => candidate.BlobSha256).HasMaxLength(64).IsRequired();
            entity.Property(candidate => candidate.Kind).HasMaxLength(64).IsRequired();
            entity.Property(candidate => candidate.State).HasMaxLength(32).HasDefaultValue(StorageCleanupCandidate.PendingState).IsRequired();
            entity.Property(candidate => candidate.Error).HasMaxLength(1000);
            entity.HasIndex(candidate => new { candidate.BlobSha256, candidate.Kind }).IsUnique();
            entity.HasIndex(candidate => candidate.State);
        });

        modelBuilder.Entity<FileVersionMetadata>(entity =>
        {
            entity.ToTable("file_version_metadata");
            entity.HasKey(metadata => metadata.FileVersionId);
            entity.Property(metadata => metadata.State).HasMaxLength(32).IsRequired();
            entity.Property(metadata => metadata.ExtractorVersion).HasMaxLength(64);
            entity.Property(metadata => metadata.ErrorCode).HasMaxLength(128);
            entity.Property(metadata => metadata.ErrorMessage).HasMaxLength(1000);
            entity.Property(metadata => metadata.DetectedMimeType).HasMaxLength(255);
            entity.Property(metadata => metadata.MediaFormat).HasMaxLength(64);
            entity.Property(metadata => metadata.CaptureDateSource).HasMaxLength(64).IsRequired();
            entity.Property(metadata => metadata.RawMetadataJson);
            entity.Property(metadata => metadata.RecoveryManifestState).HasMaxLength(32).IsRequired();
            entity.Property(metadata => metadata.RecoveryManifestError).HasMaxLength(1000);
            entity.Property(metadata => metadata.ImportedDescription).HasMaxLength(4000);
            entity.HasIndex(metadata => new { metadata.State, metadata.ProcessingStartedAt, metadata.FileVersionId });
            entity.HasIndex(metadata => new { metadata.State, metadata.ProcessedAt, metadata.FileVersionId });
            entity.HasIndex(metadata => new { metadata.State, metadata.ExtractorVersion, metadata.FileVersionId });
            entity.HasIndex(metadata => new { metadata.State, metadata.RecoveryManifestState, metadata.FileVersionId });
            entity.HasOne<FileVersion>().WithOne().HasForeignKey<FileVersionMetadata>(metadata => metadata.FileVersionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FileUserState>(entity =>
        {
            entity.ToTable("file_user_states");
            entity.HasKey(state => new { state.UserId, state.FileId });
            entity.HasIndex(state => new { state.UserId, state.Favorite });
            entity.HasOne<CatalogFile>().WithMany().HasForeignKey(state => state.FileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FolderUserState>(entity =>
        {
            entity.ToTable("folder_user_states");
            entity.HasKey(state => new { state.UserId, state.FolderId });
            entity.HasIndex(state => new { state.UserId, state.Favorite });
            entity.HasOne<CatalogFolder>().WithMany().HasForeignKey(state => state.FolderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CatalogShare>(entity =>
        {
            entity.ToTable("catalog_shares", table => table.HasCheckConstraint(
                "CK_catalog_shares_exactly_one_item",
                "(CASE WHEN \"FileId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"FolderId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"AlbumId\" IS NULL THEN 0 ELSE 1 END) = 1"));
            entity.HasKey(share => share.Id);
            entity.Property(share => share.Role).HasMaxLength(32).IsRequired();
            entity.HasIndex(share => new { share.RecipientUserId, share.CreatedAt, share.Id });
            entity.HasIndex(share => new { share.FileId, share.RecipientUserId }).IsUnique();
            entity.HasIndex(share => new { share.FolderId, share.RecipientUserId }).IsUnique();
            entity.HasIndex(share => new { share.AlbumId, share.RecipientUserId }).IsUnique();
            entity.HasOne<Library>().WithMany().HasForeignKey(share => share.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CatalogFile>().WithMany().HasForeignKey(share => share.FileId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CatalogFolder>().WithMany().HasForeignKey(share => share.FolderId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Album>().WithMany().HasForeignKey(share => share.AlbumId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Album>(entity =>
        {
            entity.ToTable("albums");
            entity.HasKey(album => album.Id);
            entity.Property(album => album.Name).HasMaxLength(200).IsRequired();
            entity.Property(album => album.Description).HasMaxLength(2000);
            entity.HasIndex(album => new { album.LibraryId, album.ModifiedAt, album.Id });
            entity.HasOne<Library>().WithMany().HasForeignKey(album => album.LibraryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlbumItem>(entity =>
        {
            entity.ToTable("album_items");
            entity.HasKey(item => new { item.AlbumId, item.FileId });
            entity.HasIndex(item => new { item.AlbumId, item.AddedAt, item.FileId });
            entity.HasIndex(item => item.FileId);
            entity.HasOne<Album>().WithMany().HasForeignKey(item => item.AlbumId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CatalogFile>().WithMany().HasForeignKey(item => item.FileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TimelineEvent>(entity =>
        {
            entity.ToTable("timeline_events");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(2000);
            entity.Property(item => item.StartDate).IsRequired();
            entity.HasIndex(item => new { item.LibraryId, item.StartDate, item.Id });
            entity.HasOne<Library>().WithMany().HasForeignKey(item => item.LibraryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("tags");
            entity.HasKey(tag => tag.Id);
            entity.Property(tag => tag.Name).HasMaxLength(64).IsRequired();
            entity.Property(tag => tag.NormalizedName).HasMaxLength(64).IsRequired();
            entity.Property(tag => tag.Color).HasMaxLength(16).IsRequired();
            entity.HasIndex(tag => new { tag.LibraryId, tag.NormalizedName }).IsUnique();
            entity.HasIndex(tag => new { tag.LibraryId, tag.Name, tag.Id });
            entity.HasOne<Library>().WithMany().HasForeignKey(tag => tag.LibraryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FileTag>(entity =>
        {
            entity.ToTable("file_tags");
            entity.HasKey(item => new { item.FileId, item.TagId });
            entity.HasIndex(item => new { item.TagId, item.FileId });
            entity.HasOne<CatalogFile>().WithMany().HasForeignKey(item => item.FileId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tag>().WithMany().HasForeignKey(item => item.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FolderTag>(entity =>
        {
            entity.ToTable("folder_tags");
            entity.HasKey(item => new { item.FolderId, item.TagId });
            entity.HasIndex(item => new { item.TagId, item.FolderId });
            entity.HasOne<CatalogFolder>().WithMany().HasForeignKey(item => item.FolderId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tag>().WithMany().HasForeignKey(item => item.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UploadSession>(entity =>
        {
            entity.ToTable("upload_sessions");
            entity.HasKey(upload => upload.Id);
            entity.Property(upload => upload.Name).HasMaxLength(255).IsRequired();
            entity.Property(upload => upload.MimeType).HasMaxLength(255).IsRequired();
            entity.Property(upload => upload.StagingFileName).HasMaxLength(255).IsRequired();
            entity.Property(upload => upload.State).HasMaxLength(32).IsRequired();
            entity.HasIndex(upload => new { upload.UserId, upload.State, upload.CreatedAt });
            entity.HasIndex(upload => new { upload.CatalogFileId, upload.State, upload.CompletedAt });
            entity.HasOne<Library>().WithMany().HasForeignKey(upload => upload.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CatalogFolder>().WithMany().HasForeignKey(upload => upload.ParentFolderId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportJob>(entity =>
        {
            entity.ToTable("import_jobs");
            entity.HasKey(job => job.Id);
            entity.Property(job => job.SourceKind).HasMaxLength(64).IsRequired();
            entity.Property(job => job.SourcePath).HasMaxLength(2048).IsRequired();
            entity.Property(job => job.TargetFolderPath).HasMaxLength(2048);
            entity.Property(job => job.NameConflictPolicy).HasMaxLength(32).IsRequired();
            entity.Property(job => job.State).HasMaxLength(32).IsRequired();
            entity.Property(job => job.Error).HasMaxLength(1000);
            entity.HasIndex(job => new { job.UserId, job.CreatedAt });
            entity.HasIndex(job => new { job.HostId, job.State });
            entity.HasIndex(job => new { job.State, job.CreatedAt });
            entity.HasOne<Library>().WithMany().HasForeignKey(job => job.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CatalogFolder>().WithMany().HasForeignKey(job => job.TargetFolderId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ImportItem>(entity =>
        {
            entity.ToTable("import_items");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.SourceRelativePath).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.SidecarRelativePath).HasMaxLength(2048);
            entity.Property(item => item.Name).HasMaxLength(255).IsRequired();
            entity.Property(item => item.ImportedDescription).HasMaxLength(4000);
            entity.Property(item => item.WarningCodes).HasMaxLength(1000);
            entity.Property(item => item.State).HasMaxLength(32).IsRequired();
            entity.Property(item => item.OutcomeCode).HasMaxLength(64);
            entity.Property(item => item.Error).HasMaxLength(1000);
            entity.HasIndex(item => new { item.ImportJobId, item.SourceRelativePath }).IsUnique();
            entity.HasIndex(item => new { item.State, item.ProcessingStartedAt, item.Id });
            entity.HasOne<ImportJob>().WithMany().HasForeignKey(item => item.ImportJobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CatalogFile>().WithMany().HasForeignKey(item => item.CatalogFileId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CacheBuildJob>(entity =>
        {
            entity.ToTable("cache_build_jobs");
            entity.HasKey(job => job.Id);
            entity.Property(job => job.State).HasMaxLength(32).IsRequired();
            entity.Property(job => job.Error).HasMaxLength(1000);
            entity.HasIndex(job => new { job.State, job.CreatedAt });
            entity.HasIndex(job => new { job.RequestedByUserId, job.CreatedAt });
            entity.HasIndex(job => new { job.HostId, job.State });
        });

        modelBuilder.Entity<ServerHost>(entity =>
        {
            entity.ToTable("hosts");
            entity.HasKey(host => host.Id);
            entity.Property(host => host.Id).ValueGeneratedNever();
            entity.Property(host => host.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<ClusterSettings>(entity =>
        {
            entity.ToTable("cluster_settings", table => table.HasCheckConstraint("CK_cluster_settings_singleton", "\"Id\" = 1"));
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Id).ValueGeneratedNever();
            entity.Property(settings => settings.LastBackupSetId).HasMaxLength(64);
            entity.Property(settings => settings.LastBackupFailureCode).HasMaxLength(64);
            entity.HasOne<ServerHost>().WithMany().HasForeignKey(settings => settings.PrimaryHostId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CacheBuildItem>(entity =>
        {
            entity.ToTable("cache_build_items");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.BlobSha256).HasMaxLength(64).IsRequired();
            entity.Property(item => item.MimeType).HasMaxLength(255).IsRequired();
            entity.Property(item => item.State).HasMaxLength(32).IsRequired();
            entity.Property(item => item.Error).HasMaxLength(1000);
            entity.HasIndex(item => new { item.CacheBuildJobId, item.BlobSha256 }).IsUnique();
            entity.HasIndex(item => new { item.State, item.ProcessingStartedAt, item.Id });
            entity.HasOne<CacheBuildJob>().WithMany().HasForeignKey(item => item.CacheBuildJobId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
