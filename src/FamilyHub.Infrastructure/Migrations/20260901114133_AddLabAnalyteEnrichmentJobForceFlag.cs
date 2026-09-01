using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLabAnalyteEnrichmentJobForceFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Force",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Force",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");
        }
    }
}
