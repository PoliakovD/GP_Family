using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchCacheDisplayName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                schema: "kb",
                table: "medication_search_cache",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                schema: "kb",
                table: "lab_analyte_search_cache",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            // Бэкфилл названия для людей у уже накопленного кэша: справочник (то же название и биоматериал) →
            // последнее название из бланка у задачи обогащения с тем же ключом → иначе остаётся null (в админке —
            // ключ с пометкой «нет названия», можно вписать руками).
            migrationBuilder.Sql("""
                UPDATE kb.lab_analyte_search_cache c
                SET "DisplayName" = LEFT(COALESCE(
                    (SELECT k."DisplayName" FROM kb.global_lab_analytes_kb k
                     WHERE k."NormalizedName" = c."NormalizedName" AND k."SpecimenKbId" = c."SpecimenKbId"
                       AND k."DisplayName" <> '' LIMIT 1),
                    (SELECT j."SourceDisplayName" FROM medical."LabAnalyteEnrichmentJobs" j
                     WHERE j."NormalizedName" = c."NormalizedName" AND j."SourceDisplayName" <> ''
                     ORDER BY (j."SpecimenKbId" = c."SpecimenKbId") DESC, j."CreatedAt" DESC LIMIT 1)), 300)
                WHERE c."DisplayName" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE kb.medication_search_cache c
                SET "DisplayName" = LEFT(COALESCE(
                    (SELECT k."DisplayName" FROM kb.global_medications_kb k
                     WHERE k."NormalizedName" = c."NormalizedName" AND k."DisplayName" <> '' LIMIT 1),
                    (SELECT x."SourceDisplayName" FROM (
                        SELECT j."SourceDisplayName", j."CreatedAt" FROM medical."MedicationEnrichmentJobs" j
                        WHERE j."NormalizedName" = c."NormalizedName"
                        UNION ALL
                        SELECT v."SourceDisplayName", v."CreatedAt" FROM medical."VisitMedicationEnrichmentJobs" v
                        WHERE v."NormalizedName" = c."NormalizedName") x
                     WHERE x."SourceDisplayName" <> '' ORDER BY x."CreatedAt" DESC LIMIT 1)), 300)
                WHERE c."DisplayName" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayName",
                schema: "kb",
                table: "medication_search_cache");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                schema: "kb",
                table: "lab_analyte_search_cache");
        }
    }
}
