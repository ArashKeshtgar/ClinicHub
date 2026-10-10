using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "Account",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Credentials = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsPlatformAdmin = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastLoginUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LockoutEndUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    TwoFactorSecret = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorLastStep = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Account", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Clinic",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slug = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DatabaseName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ConnectionString = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clinic", x => x.Id);
                    table.CheckConstraint("CK_Clinic_Slug", "Slug NOT LIKE '%[^a-z0-9-]%' AND LEN(Slug) >= 2");
                });

            migrationBuilder.CreateTable(
                name: "Role",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClinicId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Permissions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RequireTwoFactor = table.Column<bool>(type: "bit", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Role", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Role_Clinic_ClinicId",
                        column: x => x.ClinicId,
                        principalSchema: "catalog",
                        principalTable: "Clinic",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvent",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClinicId = table.Column<int>(type: "int", nullable: true),
                    ActorAccountId = table.Column<int>(type: "int", nullable: true),
                    TargetAccountId = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityEvent_Account_ActorAccountId",
                        column: x => x.ActorAccountId,
                        principalSchema: "catalog",
                        principalTable: "Account",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityEvent_Account_TargetAccountId",
                        column: x => x.TargetAccountId,
                        principalSchema: "catalog",
                        principalTable: "Account",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityEvent_Clinic_ClinicId",
                        column: x => x.ClinicId,
                        principalSchema: "catalog",
                        principalTable: "Clinic",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccountRole",
                schema: "catalog",
                columns: table => new
                {
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountRole", x => new { x.AccountId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AccountRole_Account_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "catalog",
                        principalTable: "Account",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountRole_Role_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "catalog",
                        principalTable: "Role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Account_UserName",
                schema: "catalog",
                table: "Account",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountRole_RoleId",
                schema: "catalog",
                table: "AccountRole",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Clinic_DatabaseName",
                schema: "catalog",
                table: "Clinic",
                column: "DatabaseName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clinic_Slug",
                schema: "catalog",
                table: "Clinic",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Role_ClinicId_Name",
                schema: "catalog",
                table: "Role",
                columns: new[] { "ClinicId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvent_ActorAccountId",
                schema: "catalog",
                table: "SecurityEvent",
                column: "ActorAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvent_AtUtc",
                schema: "catalog",
                table: "SecurityEvent",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvent_ClinicId_AtUtc",
                schema: "catalog",
                table: "SecurityEvent",
                columns: new[] { "ClinicId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvent_TargetAccountId",
                schema: "catalog",
                table: "SecurityEvent",
                column: "TargetAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountRole",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SecurityEvent",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Role",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Account",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Clinic",
                schema: "catalog");
        }
    }
}
