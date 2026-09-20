using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeferredEnrichmentStatusAndWebSearchValve : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VisitMedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_MedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_SpecimenKbId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.CreateTable(
                name: "WebSearchConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPaused = table.Column<bool>(type: "boolean", nullable: false),
                    PausedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebSearchConfigs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisitMedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                column: "NormalizedName",
                unique: true,
                filter: "\"Status\" IN (0, 1, 5)");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                column: "NormalizedName",
                unique: true,
                filter: "\"Status\" IN (0, 1, 5)");

            migrationBuilder.CreateIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_SpecimenKbId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                columns: new[] { "NormalizedName", "SpecimenKbId" },
                unique: true,
                filter: "\"Status\" IN (0, 1, 5)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebSearchConfigs");

            migrationBuilder.DropIndex(
                name: "IX_VisitMedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_MedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_SpecimenKbId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.CreateIndex(
                name: "IX_VisitMedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                column: "NormalizedName",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                column: "NormalizedName",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_SpecimenKbId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                columns: new[] { "NormalizedName", "SpecimenKbId" },
                unique: true,
                filter: "\"Status\" IN (0, 1)");
        }
    }
}
