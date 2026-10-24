using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllergiesAndRenewals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlertNote",
                schema: "orders",
                table: "Prescription",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RenewsPrescriptionId",
                schema: "orders",
                table: "Prescription",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PatientAllergy",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    AllergenKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Substance = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reaction = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    NoKnown = table.Column<bool>(type: "bit", nullable: false),
                    RecordedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndedByUserId = table.Column<int>(type: "int", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientAllergy", x => x.Id);
                    table.CheckConstraint("CK_PatientAllergy_Severity", "Severity IN ('Mild','Moderate','Severe')");
                    table.ForeignKey(
                        name: "FK_PatientAllergy_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientAllergy_User_EndedByUserId",
                        column: x => x.EndedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PatientAllergy_User_RecordedByUserId",
                        column: x => x.RecordedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Prescription_RenewsPrescriptionId",
                schema: "orders",
                table: "Prescription",
                column: "RenewsPrescriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientAllergy_EndedByUserId",
                schema: "people",
                table: "PatientAllergy",
                column: "EndedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientAllergy_PatientId",
                schema: "people",
                table: "PatientAllergy",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientAllergy_RecordedByUserId",
                schema: "people",
                table: "PatientAllergy",
                column: "RecordedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Prescription_Prescription_RenewsPrescriptionId",
                schema: "orders",
                table: "Prescription",
                column: "RenewsPrescriptionId",
                principalSchema: "orders",
                principalTable: "Prescription",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Prescription_Prescription_RenewsPrescriptionId",
                schema: "orders",
                table: "Prescription");

            migrationBuilder.DropTable(
                name: "PatientAllergy",
                schema: "people");

            migrationBuilder.DropIndex(
                name: "IX_Prescription_RenewsPrescriptionId",
                schema: "orders",
                table: "Prescription");

            migrationBuilder.DropColumn(
                name: "AlertNote",
                schema: "orders",
                table: "Prescription");

            migrationBuilder.DropColumn(
                name: "RenewsPrescriptionId",
                schema: "orders",
                table: "Prescription");
        }
    }
}
