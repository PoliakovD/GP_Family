using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalSpecimenAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // kb.global_specimens_kb: синонимы для мерджа дублей из админки (KbMergeService) —
            // тот же паттерн, что global_lab_analytes_kb/global_medications_kb.Aliases
            // (AddMedicalDocumentExtraction/AddMedicationEnrichment).
            migrationBuilder.Sql(
                "ALTER TABLE kb.global_specimens_kb ADD COLUMN \"Aliases\" text[] NOT NULL DEFAULT '{}';");
            migrationBuilder.Sql(
                """CREATE INDEX "IX_global_specimens_kb_Aliases" ON kb.global_specimens_kb USING GIN ("Aliases");""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS kb."IX_global_specimens_kb_Aliases";""");
            migrationBuilder.Sql("ALTER TABLE kb.global_specimens_kb DROP COLUMN \"Aliases\";");
        }
    }
}
