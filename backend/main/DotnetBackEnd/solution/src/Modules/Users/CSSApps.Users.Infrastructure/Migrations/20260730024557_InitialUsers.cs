using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CSSApps.Users.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DeletedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DeletedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "security_audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DeletedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OAuthProviderId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    OAuthProvider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AccountStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "Invited"),
                    AllowedLoginMethods = table.Column<int>(type: "INTEGER", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsServiceAccount = table.Column<bool>(type: "INTEGER", nullable: false),
                    SecurityStamp = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    DateOfBirth = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Nationality = table.Column<string>(type: "TEXT", nullable: true),
                    PreferredLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    TimeZone = table.Column<string>(type: "TEXT", nullable: true),
                    IsEmailVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPhoneVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name_FirstName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Name_LastName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Phone_CountryCode = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    Phone_Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DeletedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                columns: table => new
                {
                    permission_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    role_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.permission_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_role_permissions_permissions_permission_id",
                        column: x => x.permission_id,
                        principalTable: "permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DeletedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_security_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    role_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.role_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    LastActivity = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirstLoginAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LoginCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    TerminatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TerminationReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DeletedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_sessions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TokenType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Jti = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DeletedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_tokens_user_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "user_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_user_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "permissions",
                columns: new[] { "Id", "CreatedBy", "CreatedDate", "DeletedBy", "DeletedDate", "Description", "Name", "Status", "UpdatedBy", "UpdatedDate" },
                values: new object[,]
                {
                    { new Guid("7ca69cf0-d5b1-4db0-93e8-62714da2cd09"), "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Can read user data", "users.read", "ACTIVE", "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("9cb8d685-a9bf-4c78-9ec2-f3790428c35d"), "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Can manage roles", "roles.manage", "ACTIVE", "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("e0581e2b-c663-4da2-8005-6600627fd589"), "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Can modify user data", "users.write", "ACTIVE", "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "Id", "CreatedBy", "CreatedDate", "DeletedBy", "DeletedDate", "Description", "Name", "Status", "UpdatedBy", "UpdatedDate" },
                values: new object[,]
                {
                    { new Guid("1b7defa3-89c5-4bd1-887d-5317d2b7ed3e"), "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Family library owner", "OWNER", "ACTIVE", "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("ff9257e3-b3a3-4e08-8537-e0f551568bcb"), "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Family library member", "MEMBER", "ACTIVE", "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.InsertData(
                table: "role_permissions",
                columns: new[] { "permission_id", "role_id" },
                values: new object[,]
                {
                    { new Guid("7ca69cf0-d5b1-4db0-93e8-62714da2cd09"), new Guid("1b7defa3-89c5-4bd1-887d-5317d2b7ed3e") },
                    { new Guid("9cb8d685-a9bf-4c78-9ec2-f3790428c35d"), new Guid("1b7defa3-89c5-4bd1-887d-5317d2b7ed3e") },
                    { new Guid("e0581e2b-c663-4da2-8005-6600627fd589"), new Guid("1b7defa3-89c5-4bd1-887d-5317d2b7ed3e") }
                });

            migrationBuilder.CreateIndex(
                name: "IX_permissions_Name",
                table: "permissions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_role_id",
                table: "role_permissions",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_roles_Name",
                table: "roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_audit_events_OccurredAt",
                table: "security_audit_events",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_security_audit_events_TargetUserId_OccurredAt",
                table: "security_audit_events",
                columns: new[] { "TargetUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_security_tokens_SecretHash",
                table: "security_tokens",
                column: "SecretHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_tokens_UserId_Purpose_UsedAt_ExpiresAt",
                table: "security_tokens",
                columns: new[] { "UserId", "Purpose", "UsedAt", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_user_id",
                table: "user_roles",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_sessions_UserId_IsActive_LastActivity",
                table: "user_sessions",
                columns: new[] { "UserId", "IsActive", "LastActivity" });

            migrationBuilder.CreateIndex(
                name: "IX_user_tokens_ExpiresAt",
                table: "user_tokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_user_tokens_IsRevoked",
                table: "user_tokens",
                column: "IsRevoked");

            migrationBuilder.CreateIndex(
                name: "IX_user_tokens_Jti",
                table: "user_tokens",
                column: "Jti",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_tokens_SessionId_TokenType_IsRevoked",
                table: "user_tokens",
                columns: new[] { "SessionId", "TokenType", "IsRevoked" });

            migrationBuilder.CreateIndex(
                name: "IX_user_tokens_TokenHash",
                table: "user_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_tokens_UserId_TokenType_IsRevoked",
                table: "user_tokens",
                columns: new[] { "UserId", "TokenType", "IsRevoked" });

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_IsServiceAccount_AccountStatus",
                table: "users",
                columns: new[] { "IsServiceAccount", "AccountStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_users_OAuthProvider_OAuthProviderId",
                table: "users",
                columns: new[] { "OAuthProvider", "OAuthProviderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_permissions");

            migrationBuilder.DropTable(
                name: "security_audit_events");

            migrationBuilder.DropTable(
                name: "security_tokens");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "user_tokens");

            migrationBuilder.DropTable(
                name: "permissions");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "user_sessions");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
