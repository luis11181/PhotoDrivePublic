using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaExplorer.Media.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHostsAndClusterSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HostId",
                table: "import_jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HostId",
                table: "cache_build_jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "hosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    PrimaryHostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PrimaryChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PrimaryChangedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastBackupVerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastBackupSetId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastBackupFailedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastBackupFailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BackupConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_settings", x => x.Id);
                    table.CheckConstraint("CK_cluster_settings_singleton", "\"Id\" = 1");
                    table.ForeignKey(
                        name: "FK_cluster_settings_hosts_PrimaryHostId",
                        column: x => x.PrimaryHostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_HostId_State",
                table: "import_jobs",
                columns: new[] { "HostId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_cache_build_jobs_HostId_State",
                table: "cache_build_jobs",
                columns: new[] { "HostId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_cluster_settings_PrimaryHostId",
                table: "cluster_settings",
                column: "PrimaryHostId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cluster_settings");

            migrationBuilder.DropTable(
                name: "hosts");

            migrationBuilder.DropIndex(
                name: "IX_import_jobs_HostId_State",
                table: "import_jobs");

            migrationBuilder.DropIndex(
                name: "IX_cache_build_jobs_HostId_State",
                table: "cache_build_jobs");

            migrationBuilder.DropColumn(
                name: "HostId",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "HostId",
                table: "cache_build_jobs");
        }
    }
}
