using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentJobUnitsAndPlausibilityRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Units",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // analysis.analyte-plausibility теперь проверяет и извлечённые из документа показатели:
            // новая активная версия = активная + правило про оборванные названия и слова из шапки
            // бланка (копия через replace — сохраняет ручные правки админа).
            migrationBuilder.Sql($"""
                WITH old AS (
                    UPDATE "PipelinePromptVersions" SET "IsActive" = FALSE
                    WHERE "PromptId" = '{PromptId}' AND "IsActive" = TRUE
                    RETURNING "Body"
                )
                INSERT INTO "PipelinePromptVersions" ("Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt")
                SELECT '{VersionId}', '{PromptId}',
                       (SELECT COALESCE(MAX("Version"), 0) + 1 FROM "PipelinePromptVersions" WHERE "PromptId" = '{PromptId}'),
                       replace(old."Body", '{Esc(Anchor)}', '{Esc(Rule + Anchor)}'), TRUE,
                       'Оборванные названия и слова из шапки бланка — valid=false; проверка для всех происхождений.',
                       '2026-10-03T00:00:00.0000000Z'
                FROM old LIMIT 1;
                """);
        }

        private static readonly Guid PromptId = new("2d3e4f50-6172-4839-9a0b-1c2d3e4f5a6b");
        private static readonly Guid VersionId = new("5c0d7e21-8f3a-4b69-a1d2-93e4f6a7b8c0");
        private const string Anchor = "- \"reason\" — короткая причина отказа";
        private const string Rule =
            "- \"valid\": false также для НЕПОЛНОГО названия: оборванного на полуслове или на предлоге " +
            "(\"Тромбоцитарный кри\", \"Средняя концентрация гемоглобина в\") — это перенос строки в бланке, " +
            "а не показатель; в \"reason\" так и напиши. Для слова из шапки документа (\"Пациент\", \"Врач\", \"Дата\") — " +
            "тоже false, и \"confidence\" ставь 0.\n        ";
        private static string Esc(string v) => v.Replace("'", "''");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE FROM "PipelinePromptVersions" WHERE "Id" = '{VersionId}';
                """);
            migrationBuilder.Sql($"""
                UPDATE "PipelinePromptVersions" SET "IsActive" = TRUE
                WHERE "PromptId" = '{PromptId}' AND "Version" = (
                    SELECT MAX("Version") FROM "PipelinePromptVersions" WHERE "PromptId" = '{PromptId}');
                """);

            migrationBuilder.DropColumn(
                name: "Units",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");
        }
    }
}
