using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveJobCurrentThought : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentThought",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "CurrentThought",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "CurrentThought",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs");

            migrationBuilder.DropColumn(
                name: "CurrentThought",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentThought",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentThought",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentThought",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentThought",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "text",
                nullable: true);
        }
    }
}
