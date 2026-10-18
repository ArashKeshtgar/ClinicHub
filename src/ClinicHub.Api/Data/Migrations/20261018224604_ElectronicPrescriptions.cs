using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ElectronicPrescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PrescriptionId",
                schema: "orders",
                table: "OutboxMessage",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Prescription",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EncounterId = table.Column<int>(type: "int", nullable: false),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    PrescriberProviderId = table.Column<int>(type: "int", nullable: false),
                    Din = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Display = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Directions = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    QuantityUnit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DaysSupply = table.Column<int>(type: "int", nullable: true),
                    Repeats = table.Column<int>(type: "int", nullable: false),
                    NoSubstitution = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    GroupNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OutboxMessageId = table.Column<long>(type: "bigint", nullable: true),
                    ExternalId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CancelledUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    StatusNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prescription", x => x.Id);
                    table.CheckConstraint("CK_Prescription_DaysSupply", "DaysSupply IS NULL OR DaysSupply BETWEEN 1 AND 365");
                    table.CheckConstraint("CK_Prescription_Din", "Din NOT LIKE '%[^0-9]%' AND LEN(Din) = 8");
                    table.CheckConstraint("CK_Prescription_Quantity", "Quantity > 0");
                    table.CheckConstraint("CK_Prescription_Repeats", "Repeats BETWEEN 0 AND 11");
                    table.CheckConstraint("CK_Prescription_Status", "Status IN ('Draft','Accepted','Cancelled','Failed','Cancelling')");
                    table.ForeignKey(
                        name: "FK_Prescription_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalSchema: "care",
                        principalTable: "Encounter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Prescription_OutboxMessage_OutboxMessageId",
                        column: x => x.OutboxMessageId,
                        principalSchema: "orders",
                        principalTable: "OutboxMessage",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Prescription_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Prescription_Provider_PrescriberProviderId",
                        column: x => x.PrescriberProviderId,
                        principalSchema: "clinic",
                        principalTable: "Provider",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_PrescriptionId",
                schema: "orders",
                table: "OutboxMessage",
                column: "PrescriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Prescription_EncounterId",
                schema: "orders",
                table: "Prescription",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_Prescription_OutboxMessageId",
                schema: "orders",
                table: "Prescription",
                column: "OutboxMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Prescription_PatientId_CreatedUtc",
                schema: "orders",
                table: "Prescription",
                columns: new[] { "PatientId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Prescription_PrescriberProviderId",
                schema: "orders",
                table: "Prescription",
                column: "PrescriberProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_OutboxMessage_Prescription_PrescriptionId",
                schema: "orders",
                table: "OutboxMessage",
                column: "PrescriptionId",
                principalSchema: "orders",
                principalTable: "Prescription",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OutboxMessage_Prescription_PrescriptionId",
                schema: "orders",
                table: "OutboxMessage");

            migrationBuilder.DropTable(
                name: "Prescription",
                schema: "orders");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessage_PrescriptionId",
                schema: "orders",
                table: "OutboxMessage");

            migrationBuilder.DropColumn(
                name: "PrescriptionId",
                schema: "orders",
                table: "OutboxMessage");
        }
    }
}
