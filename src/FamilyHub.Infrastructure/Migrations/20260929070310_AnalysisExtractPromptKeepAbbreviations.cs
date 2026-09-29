using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Новая активная версия "analysis.extract": копия текущей активной с добавленным правилом
    // "аббревиатуры (СРБ, АЧТВ, МНО...) оставлять как в бланке" — модель по прежнему правилу
    // "литературный регистр" превращала "АЧТВ" в "Ачтв". Копируем активное тело через replace(),
    // а не хардкодим целиком: сохраняет ручные правки админа; если якорной фразы в теле нет
    // (админ переписал абзац), версия просто копия — безвредно.
    public partial class AnalysisExtractPromptKeepAbbreviations : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
        private static readonly Guid AnalysisExtractPromptId = new("45508844-3607-4c68-b345-b34ca62d67f4");
        private static readonly Guid NewVersionId = new("7b1e3c52-9a4d-4f60-8c2b-5d9e0a1f3b74");

        private const string Anchor = "даже если в бланке весь текст напечатан КАПСОМ.";
        private const string Replacement =
            "даже если в бланке весь текст напечатан КАПСОМ. Исключение — АББРЕВИАТУРЫ (СРБ, АЧТВ, МНО, ТТГ, HbA1c, IgG): " +
            "оставляй ровно как в бланке, не переводи в \"Срб\"/\"Ачтв\".";
        private const string Note = "Аббревиатуры (СРБ, АЧТВ...) сохраняются как в бланке, не приводятся к \"Срб\".";

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
                       replace(old."Body", '{Esc(Anchor)}', '{Esc(Replacement)}'), TRUE,
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
