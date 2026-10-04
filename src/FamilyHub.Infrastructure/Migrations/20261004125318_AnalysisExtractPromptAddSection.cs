using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Новая активная версия "analysis.extract": поле "section" (заголовок раздела бланка, под которым
    // стоит показатель → LabIndicator.PanelLabel) в формате ответа и правило к нему. Нужно в первую
    // очередь vision-пути (фото/скан): на текстовом пути раздел берёт детерминированный
    // LabTableRowDetector, ответ модели — только запасной вариант. Тот же приём, что
    // AnalysisExtractPromptKeepAbbreviations: копия активного тела через replace() по якорям, чтобы
    // сохранить ручные правки админа; если якоря в теле нет (админ переписал абзац) — версия просто
    // копия, безвредно (section останется null). Текст совпадает с LmStudioMedicalDocumentExtractor.AnalysisSystemPrompt.
    public partial class AnalysisExtractPromptAddSection : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
        private static readonly Guid AnalysisExtractPromptId = new("45508844-3607-4c68-b345-b34ca62d67f4");
        private static readonly Guid NewVersionId = new("55a0460f-c05d-47a0-b7b8-c49ce84f6dfd");

        private const string FormatAnchor =
            "\"refExpected\": \"ожидаемый НОРМАЛЬНЫЙ результат этого показателя по общемедицинским знаниям — заполняй ТОЛЬКО если в бланке референса нет вовсе (ни числом, ни текстом); если референс в бланке есть — всегда null\"";
        private const string FormatReplacement = FormatAnchor + ",\n" +
            "      \"section\": \"заголовок раздела бланка, под которым напечатан показатель (например, \\\"Лейкоцитарная формула\\\", \\\"Гормоны щитовидной железы\\\"), или null\"";

        private const string RuleAnchor = "- \"documentDate\"/\"doctor\" — заполняй, только если";
        private const string RuleReplacement =
            "- \"section\" — ТОЛЬКО напечатанный в бланке заголовок раздела/группы показателей, под которым\n" +
            "  стоит этот показатель (\"Общий анализ крови\", \"Лейкоцитарная формула\", \"Биохимия\"). Это НЕ\n" +
            "  биоматериал (\"кровь\", \"моча\") и НЕ название показателя. Если разделов в бланке нет или\n" +
            "  заголовок не виден — null; не придумывай раздел по смыслу показателя.\n" +
            RuleAnchor;

        private const string Note = "Поле \"section\" — раздел бланка, под которым стоит показатель (группировка в UI, не ключ).";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Один оператор: частичный уникальный индекс допускает одну активную версию на промпт,
            // поэтому старая гасится и отдаёт тело через RETURNING, а новая вставляется из него.
            migrationBuilder.Sql($"""
                WITH old AS (
                    UPDATE "PipelinePromptVersions" SET "IsActive" = FALSE
                    WHERE "PromptId" = '{AnalysisExtractPromptId}' AND "IsActive" = TRUE
                    RETURNING "Body"
                )
                INSERT INTO "PipelinePromptVersions" ("Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt")
                SELECT '{NewVersionId}', '{AnalysisExtractPromptId}',
                       (SELECT COALESCE(MAX("Version"), 0) + 1 FROM "PipelinePromptVersions" WHERE "PromptId" = '{AnalysisExtractPromptId}'),
                       replace(replace(old."Body", '{Esc(FormatAnchor)}', '{Esc(FormatReplacement)}'), '{Esc(RuleAnchor)}', '{Esc(RuleReplacement)}'), TRUE,
                       '{Esc(Note)}', '{SeedCreatedAt:O}'
                FROM old LIMIT 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE FROM "PipelinePromptVersions" WHERE "Id" = '{NewVersionId}';
                """);
            migrationBuilder.Sql($"""
                UPDATE "PipelinePromptVersions" SET "IsActive" = TRUE
                WHERE "PromptId" = '{AnalysisExtractPromptId}' AND "Version" = (
                    SELECT MAX("Version") FROM "PipelinePromptVersions" WHERE "PromptId" = '{AnalysisExtractPromptId}');
                """);
        }

        private static string Esc(string value) => value.Replace("'", "''");
    }
}
