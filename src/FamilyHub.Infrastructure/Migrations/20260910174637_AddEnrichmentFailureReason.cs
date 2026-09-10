using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentFailureReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailureReason",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureReason",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureReason",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureReason",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailureReason",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");
        }
    }
}
