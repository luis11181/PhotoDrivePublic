using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaExplorer.Media.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemovePhotoLibraryMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "photo_library_items");

            migrationBuilder.DropColumn(
                name: "AddToPhotos",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "PhotoMembershipRequested",
                table: "file_version_metadata");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AddToPhotos",
                table: "upload_sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PhotoMembershipRequested",
                table: "file_version_metadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "photo_library_items",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_photo_library_items_AddedAt_FileId",
                table: "photo_library_items",
                columns: new[] { "AddedAt", "FileId" });
        }
    }
}
