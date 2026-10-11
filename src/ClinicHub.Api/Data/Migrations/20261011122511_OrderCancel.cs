using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                schema: "orders",
                table: "ServiceRequest",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledUtc",
                schema: "orders",
                table: "ServiceRequest",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusNote",
                schema: "orders",
                table: "ServiceRequest",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelReason",
                schema: "orders",
                table: "ServiceRequest");

            migrationBuilder.DropColumn(
                name: "CancelledUtc",
                schema: "orders",
                table: "ServiceRequest");

            migrationBuilder.DropColumn(
                name: "StatusNote",
                schema: "orders",
                table: "ServiceRequest");
        }
    }
}
