using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJobIsTransientFailure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTransientFailure",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTransientFailure",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTransientFailure",
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
                name: "IsTransientFailure",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "IsTransientFailure",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs");

            migrationBuilder.DropColumn(
                name: "IsTransientFailure",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");
        }
    }
}
