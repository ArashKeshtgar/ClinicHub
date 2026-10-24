using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PatientMergeLog",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SurvivorId = table.Column<int>(type: "int", nullable: false),
                    MergedId = table.Column<int>(type: "int", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ByUserId = table.Column<int>(type: "int", nullable: false),
                    MovedJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FieldsBeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FieldsAfterJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OutboxIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UndoneUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UndoneByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientMergeLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatientMergeLog_Patient_MergedId",
                        column: x => x.MergedId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PatientMergeLog_Patient_SurvivorId",
                        column: x => x.SurvivorId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PatientMergeLog_User_ByUserId",
                        column: x => x.ByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PatientMergeLog_User_UndoneByUserId",
                        column: x => x.UndoneByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PatientMergeLog_ByUserId",
                schema: "people",
                table: "PatientMergeLog",
                column: "ByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientMergeLog_MergedId",
                schema: "people",
                table: "PatientMergeLog",
                column: "MergedId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientMergeLog_SurvivorId",
                schema: "people",
                table: "PatientMergeLog",
                column: "SurvivorId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientMergeLog_UndoneByUserId",
                schema: "people",
                table: "PatientMergeLog",
                column: "UndoneByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatientMergeLog",
                schema: "people");
        }
    }
}
