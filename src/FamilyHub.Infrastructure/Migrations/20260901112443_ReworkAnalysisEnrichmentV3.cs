using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReworkAnalysisEnrichmentV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_global_lab_analytes_kb_NormalizedName",
                schema: "kb",
                table: "global_lab_analytes_kb");

            migrationBuilder.AddColumn<int>(
                name: "Specimen",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Specimen",
                schema: "kb",
                table: "global_lab_analytes_kb",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "lab_analyte_search_cache",
                schema: "kb",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Specimen = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CanBeUpdatedAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SnippetsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_analyte_search_cache", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_Specimen",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                columns: new[] { "NormalizedName", "Specimen" },
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_global_lab_analytes_kb_NormalizedName_Specimen",
                schema: "kb",
                table: "global_lab_analytes_kb",
                columns: new[] { "NormalizedName", "Specimen" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lab_analyte_search_cache_NormalizedName_Specimen",
                schema: "kb",
                table: "lab_analyte_search_cache",
                columns: new[] { "NormalizedName", "Specimen" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_analyte_search_cache",
                schema: "kb");

            migrationBuilder.DropIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_Specimen",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_global_lab_analytes_kb_NormalizedName_Specimen",
                schema: "kb",
                table: "global_lab_analytes_kb");

            migrationBuilder.DropColumn(
                name: "Specimen",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "Specimen",
                schema: "kb",
                table: "global_lab_analytes_kb");

            migrationBuilder.CreateIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                column: "NormalizedName",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_global_lab_analytes_kb_NormalizedName",
                schema: "kb",
                table: "global_lab_analytes_kb",
                column: "NormalizedName",
                unique: true);
        }
    }
}
