using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ServiceRequest_TargetSystem_ExternalId",
                schema: "orders",
                table: "ServiceRequest");

            migrationBuilder.AddColumn<long>(
                name: "OutboxMessageId",
                schema: "orders",
                table: "ServiceRequest",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequest_OutboxMessageId",
                schema: "orders",
                table: "ServiceRequest",
                column: "OutboxMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequest_TargetSystem_ExternalId",
                schema: "orders",
                table: "ServiceRequest",
                columns: new[] { "TargetSystem", "ExternalId" },
                filter: "ExternalId IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceRequest_OutboxMessage_OutboxMessageId",
                schema: "orders",
                table: "ServiceRequest",
                column: "OutboxMessageId",
                principalSchema: "orders",
                principalTable: "OutboxMessage",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceRequest_OutboxMessage_OutboxMessageId",
                schema: "orders",
                table: "ServiceRequest");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequest_OutboxMessageId",
                schema: "orders",
                table: "ServiceRequest");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequest_TargetSystem_ExternalId",
                schema: "orders",
                table: "ServiceRequest");

            migrationBuilder.DropColumn(
                name: "OutboxMessageId",
                schema: "orders",
                table: "ServiceRequest");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequest_TargetSystem_ExternalId",
                schema: "orders",
                table: "ServiceRequest",
                columns: new[] { "TargetSystem", "ExternalId" },
                unique: true,
                filter: "ExternalId IS NOT NULL");
        }
    }
}
