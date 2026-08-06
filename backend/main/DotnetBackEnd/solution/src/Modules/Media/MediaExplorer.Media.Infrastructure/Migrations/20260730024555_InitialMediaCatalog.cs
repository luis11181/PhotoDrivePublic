using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaExplorer.Media.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialMediaCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blobs",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObjectKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LifecycleState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "available"),
                    DeletionRequestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletionLastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletionAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletionError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blobs", x => x.Sha256);
                });

            migrationBuilder.CreateTable(
                name: "cache_build_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    KeepOriginals = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SelectedItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    QueuedItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PlannedDownloadBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_build_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "libraries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NextItemSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_libraries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "storage_cleanup_candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlobSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "pending"),
                    DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_cleanup_candidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cache_build_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CacheBuildJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CatalogFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlobSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessingStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_build_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cache_build_items_cache_build_jobs_CacheBuildJobId",
                        column: x => x.CacheBuildJobId,
                        principalTable: "cache_build_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "albums",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_albums", x => x.Id);
                    table.ForeignKey(
                        name: "FK_albums_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "folders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SystemRole = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrashedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TrashRootId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_folders_folders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_folders_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tags_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CaptureDateSource = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UserCapturedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImportedCapturedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EmbeddedCapturedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FilenameCapturedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    AltitudeMeters = table.Column<double>(type: "REAL", nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    Orientation = table.Column<int>(type: "INTEGER", nullable: true),
                    MediaFormat = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MetadataState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MetadataAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MetadataErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MetadataErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    MetadataProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RecoveryManifestState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrashedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TrashRootId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_files_folders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_files_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "folder_user_states",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Favorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folder_user_states", x => new { x.UserId, x.FolderId });
                    table.ForeignKey(
                        name: "FK_folder_user_states_folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetFolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetFolderPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    NameConflictPolicy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OrphanSidecarCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IgnoredJsonCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_jobs_folders_TargetFolderId",
                        column: x => x.TargetFolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_import_jobs_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "upload_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CatalogFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AddToPhotos = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    StagingFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upload_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_upload_sessions_folders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_upload_sessions_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "folder_tags",
                columns: table => new
                {
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folder_tags", x => new { x.FolderId, x.TagId });
                    table.ForeignKey(
                        name: "FK_folder_tags_folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_folder_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "album_items",
                columns: table => new
                {
                    AlbumId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_album_items", x => new { x.AlbumId, x.FileId });
                    table.ForeignKey(
                        name: "FK_album_items_albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_album_items_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AlbumId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RecipientUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_shares", x => x.Id);
                    table.CheckConstraint("CK_catalog_shares_exactly_one_item", "(CASE WHEN \"FileId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"FolderId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"AlbumId\" IS NULL THEN 0 ELSE 1 END) = 1");
                    table.ForeignKey(
                        name: "FK_catalog_shares_albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_catalog_shares_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_catalog_shares_folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_catalog_shares_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_tags",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_tags", x => new { x.FileId, x.TagId });
                    table.ForeignKey(
                        name: "FK_file_tags_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_file_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_user_states",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Favorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_user_states", x => new { x.UserId, x.FileId });
                    table.ForeignKey(
                        name: "FK_file_user_states_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlobSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    DeclaredMimeType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    OriginalName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    UploaderUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_file_versions_blobs_BlobSha256",
                        column: x => x.BlobSha256,
                        principalTable: "blobs",
                        principalColumn: "Sha256",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_file_versions_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "photo_library_items",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_photo_library_items", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_photo_library_items_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImportJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CatalogFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceRelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    SidecarRelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ExpectedSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ImportedCapturedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImportedDescription = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ImportedLatitude = table.Column<double>(type: "REAL", nullable: true),
                    ImportedLongitude = table.Column<double>(type: "REAL", nullable: true),
                    ImportedAltitudeMeters = table.Column<double>(type: "REAL", nullable: true),
                    WarningCodes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OutcomeCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessingStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_items_files_CatalogFileId",
                        column: x => x.CatalogFileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_import_items_import_jobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "import_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_version_metadata",
                columns: table => new
                {
                    FileVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessingStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExtractorVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    DetectedMimeType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    MediaFormat = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    Orientation = table.Column<int>(type: "INTEGER", nullable: true),
                    ExifDateTimeOriginal = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExifDateTimeDigitized = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EmbeddedCreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FilenameCapturedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImportedCapturedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImportedDescription = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ImportedLatitude = table.Column<double>(type: "REAL", nullable: true),
                    ImportedLongitude = table.Column<double>(type: "REAL", nullable: true),
                    ImportedAltitudeMeters = table.Column<double>(type: "REAL", nullable: true),
                    PhotoMembershipRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    EffectiveCapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CaptureDateSource = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RawMetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    RecoveryManifestState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RecoveryManifestError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RecoveryManifestWrittenAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_version_metadata", x => x.FileVersionId);
                    table.ForeignKey(
                        name: "FK_file_version_metadata_file_versions_FileVersionId",
                        column: x => x.FileVersionId,
                        principalTable: "file_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_album_items_AlbumId_AddedAt_FileId",
                table: "album_items",
                columns: new[] { "AlbumId", "AddedAt", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_album_items_FileId",
                table: "album_items",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_albums_LibraryId_ModifiedAt_Id",
                table: "albums",
                columns: new[] { "LibraryId", "ModifiedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_blobs_LifecycleState",
                table: "blobs",
                column: "LifecycleState");

            migrationBuilder.CreateIndex(
                name: "IX_cache_build_items_CacheBuildJobId_BlobSha256",
                table: "cache_build_items",
                columns: new[] { "CacheBuildJobId", "BlobSha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cache_build_items_State_ProcessingStartedAt_Id",
                table: "cache_build_items",
                columns: new[] { "State", "ProcessingStartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_cache_build_jobs_RequestedByUserId_CreatedAt",
                table: "cache_build_jobs",
                columns: new[] { "RequestedByUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_cache_build_jobs_State_CreatedAt",
                table: "cache_build_jobs",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_shares_AlbumId_RecipientUserId",
                table: "catalog_shares",
                columns: new[] { "AlbumId", "RecipientUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_shares_FileId_RecipientUserId",
                table: "catalog_shares",
                columns: new[] { "FileId", "RecipientUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_shares_FolderId_RecipientUserId",
                table: "catalog_shares",
                columns: new[] { "FolderId", "RecipientUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_shares_LibraryId",
                table: "catalog_shares",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_shares_RecipientUserId_CreatedAt_Id",
                table: "catalog_shares",
                columns: new[] { "RecipientUserId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_file_tags_TagId_FileId",
                table: "file_tags",
                columns: new[] { "TagId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_file_user_states_FileId",
                table: "file_user_states",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_file_user_states_UserId_Favorite",
                table: "file_user_states",
                columns: new[] { "UserId", "Favorite" });

            migrationBuilder.CreateIndex(
                name: "IX_file_version_metadata_State_ExtractorVersion_FileVersionId",
                table: "file_version_metadata",
                columns: new[] { "State", "ExtractorVersion", "FileVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_file_version_metadata_State_ProcessedAt_FileVersionId",
                table: "file_version_metadata",
                columns: new[] { "State", "ProcessedAt", "FileVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_file_version_metadata_State_ProcessingStartedAt_FileVersionId",
                table: "file_version_metadata",
                columns: new[] { "State", "ProcessingStartedAt", "FileVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_file_version_metadata_State_RecoveryManifestState_FileVersionId",
                table: "file_version_metadata",
                columns: new[] { "State", "RecoveryManifestState", "FileVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_BlobSha256",
                table: "file_versions",
                column: "BlobSha256");

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_FileId",
                table: "file_versions",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_files_LibraryId_ParentFolderId_Sequence",
                table: "files",
                columns: new[] { "LibraryId", "ParentFolderId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_files_LibraryId_TrashRootId",
                table: "files",
                columns: new[] { "LibraryId", "TrashRootId" });

            migrationBuilder.CreateIndex(
                name: "IX_files_LibraryId_TrashedAt_CapturedAt_Sequence",
                table: "files",
                columns: new[] { "LibraryId", "TrashedAt", "CapturedAt", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_files_LibraryId_TrashedAt_Sequence",
                table: "files",
                columns: new[] { "LibraryId", "TrashedAt", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_files_ParentFolderId",
                table: "files",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_folder_tags_TagId_FolderId",
                table: "folder_tags",
                columns: new[] { "TagId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_folder_user_states_FolderId",
                table: "folder_user_states",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_folder_user_states_UserId_Favorite",
                table: "folder_user_states",
                columns: new[] { "UserId", "Favorite" });

            migrationBuilder.CreateIndex(
                name: "IX_folders_LibraryId_ParentFolderId_Sequence",
                table: "folders",
                columns: new[] { "LibraryId", "ParentFolderId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_folders_LibraryId_SystemRole",
                table: "folders",
                columns: new[] { "LibraryId", "SystemRole" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_folders_LibraryId_TrashRootId",
                table: "folders",
                columns: new[] { "LibraryId", "TrashRootId" });

            migrationBuilder.CreateIndex(
                name: "IX_folders_LibraryId_TrashedAt_Sequence",
                table: "folders",
                columns: new[] { "LibraryId", "TrashedAt", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_folders_ParentFolderId",
                table: "folders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_import_items_CatalogFileId",
                table: "import_items",
                column: "CatalogFileId");

            migrationBuilder.CreateIndex(
                name: "IX_import_items_ImportJobId_SourceRelativePath",
                table: "import_items",
                columns: new[] { "ImportJobId", "SourceRelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_import_items_State_ProcessingStartedAt_Id",
                table: "import_items",
                columns: new[] { "State", "ProcessingStartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_LibraryId",
                table: "import_jobs",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_State_CreatedAt",
                table: "import_jobs",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_TargetFolderId",
                table: "import_jobs",
                column: "TargetFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_UserId_CreatedAt",
                table: "import_jobs",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_libraries_OwnerUserId",
                table: "libraries",
                column: "OwnerUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_photo_library_items_AddedAt_FileId",
                table: "photo_library_items",
                columns: new[] { "AddedAt", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_storage_cleanup_candidates_BlobSha256_Kind",
                table: "storage_cleanup_candidates",
                columns: new[] { "BlobSha256", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_storage_cleanup_candidates_State",
                table: "storage_cleanup_candidates",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_tags_LibraryId_Name_Id",
                table: "tags",
                columns: new[] { "LibraryId", "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_tags_LibraryId_NormalizedName",
                table: "tags",
                columns: new[] { "LibraryId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_CatalogFileId_State_CompletedAt",
                table: "upload_sessions",
                columns: new[] { "CatalogFileId", "State", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_LibraryId",
                table: "upload_sessions",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_ParentFolderId",
                table: "upload_sessions",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_UserId_State_CreatedAt",
                table: "upload_sessions",
                columns: new[] { "UserId", "State", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "album_items");

            migrationBuilder.DropTable(
                name: "cache_build_items");

            migrationBuilder.DropTable(
                name: "catalog_shares");

            migrationBuilder.DropTable(
                name: "file_tags");

            migrationBuilder.DropTable(
                name: "file_user_states");

            migrationBuilder.DropTable(
                name: "file_version_metadata");

            migrationBuilder.DropTable(
                name: "folder_tags");

            migrationBuilder.DropTable(
                name: "folder_user_states");

            migrationBuilder.DropTable(
                name: "import_items");

            migrationBuilder.DropTable(
                name: "photo_library_items");

            migrationBuilder.DropTable(
                name: "storage_cleanup_candidates");

            migrationBuilder.DropTable(
                name: "upload_sessions");

            migrationBuilder.DropTable(
                name: "cache_build_jobs");

            migrationBuilder.DropTable(
                name: "albums");

            migrationBuilder.DropTable(
                name: "file_versions");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "import_jobs");

            migrationBuilder.DropTable(
                name: "blobs");

            migrationBuilder.DropTable(
                name: "files");

            migrationBuilder.DropTable(
                name: "folders");

            migrationBuilder.DropTable(
                name: "libraries");
        }
    }
}
