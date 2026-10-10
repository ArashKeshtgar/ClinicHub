using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OhipBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileContent",
                schema: "billing",
                table: "RemittanceAdvice",
                type: "nvarchar(max)",
                maxLength: -1,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Simulated",
                schema: "billing",
                table: "RemittanceAdvice",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OutOfProvinceNumber",
                schema: "people",
                table: "Patient",
                type: "nvarchar(12)",
                maxLength: 12,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileContent",
                schema: "billing",
                table: "RemittanceAdvice");

            migrationBuilder.DropColumn(
                name: "Simulated",
                schema: "billing",
                table: "RemittanceAdvice");

            migrationBuilder.DropColumn(
                name: "OutOfProvinceNumber",
                schema: "people",
                table: "Patient");
        }
    }
}
