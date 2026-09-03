using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <summary>
    /// Ручная правка справочника после ИИ (§3 плана) — LockedFields на обеих обезличенных таблицах
    /// (kb.global_lab_analytes_kb, kb.global_medications_kb), тот же приём вне EF-модели, что
    /// Aliases (см. GlobalLabAnalyteKbConfiguration/GlobalMedicationKbConfiguration): Postgres
    /// text[], без кроссплатформенного маппинга для SQLite-юнит-тестов, читается/пишется только
    /// raw SQL (LabAnalyteKbWriter/KbWriter/AdminCatalogEndpoints).
    /// </summary>
    public partial class AddKbLockedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE kb.global_lab_analytes_kb ADD COLUMN \"LockedFields\" text[] NOT NULL DEFAULT '{}';");
            migrationBuilder.Sql(
                "ALTER TABLE kb.global_medications_kb ADD COLUMN \"LockedFields\" text[] NOT NULL DEFAULT '{}';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE kb.global_lab_analytes_kb DROP COLUMN \"LockedFields\";");
            migrationBuilder.Sql("ALTER TABLE kb.global_medications_kb DROP COLUMN \"LockedFields\";");
        }
    }
}
