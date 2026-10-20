using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExternalLabTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequest_Target",
                schema: "orders",
                table: "ServiceRequest");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequest_Target",
                schema: "orders",
                table: "ServiceRequest",
                sql: "TargetSystem IN ('LabFlow','ClinicReport','ExternalLab')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequest_Target",
                schema: "orders",
                table: "ServiceRequest");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequest_Target",
                schema: "orders",
                table: "ServiceRequest",
                sql: "TargetSystem IN ('LabFlow','ClinicReport')");
        }
    }
}
