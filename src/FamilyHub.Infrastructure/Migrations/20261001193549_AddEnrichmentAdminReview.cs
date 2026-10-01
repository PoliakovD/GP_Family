using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // ADR-0018: ручное одобрение платных поисков и результатов обогащения. Схема — статусы 6/7 в
    // частичных уникальных индексах дедупа, поля задач, таблица порогов. Данные — новые активные
    // версии четырёх промптов, требующие от модели `confidence`/`confidenceReason`: копия текущей
    // активной версии (сохраняет ручные правки админа) + дополнение в конце. Дополнение, а не
    // замена якорной фразы: тела могли быть переписаны из админки, дописанный блок работает с любым.
    public partial class AddEnrichmentAdminReview : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly (string Key, Guid VersionId, string Addendum, string Note)[] ConfidencePrompts =
        [
            ("guard.legitimacy-check", new Guid("3e0a7c11-52d4-4b8e-9a6f-0c1d2e3f4a51"),
                "Дополнение (оценка уверенности): в JSON-ответ ВСЕГДА добавляй поля \"confidence\" — число от 0 до 1, " +
                "твоя уверенность, что текст — реальное медицинское название без признаков постороннего содержимого " +
                "(ниже ~0.7, если название незнакомое, искажённое, слишком общее или может оказаться не тем, чем кажется), " +
                "и \"confidenceReason\" — одна короткая фраза по-русски, почему такая оценка. Поля обязательны и при \"valid\": true.",
                "Добавлены обязательные поля confidence/confidenceReason (ADR-0018, этап запроса)."),
            ("analysis.analyte-plausibility", new Guid("3e0a7c11-52d4-4b8e-9a6f-0c1d2e3f4a52"),
                "Дополнение (оценка уверенности): в JSON-ответ ВСЕГДА добавляй поля \"confidence\" — число от 0 до 1, " +
                "твоя уверенность, что это реальный лабораторный показатель и сочетание с источником осмысленно " +
                "(ниже ~0.7, если название редкое, неоднозначное, похоже на опечатку или сочетание сомнительно), " +
                "и \"confidenceReason\" — одна короткая фраза по-русски, почему такая оценка. Поля обязательны и при \"valid\": true.",
                "Добавлены обязательные поля confidence/confidenceReason (ADR-0018, этап запроса)."),
            ("medication.summarize", new Guid("3e0a7c11-52d4-4b8e-9a6f-0c1d2e3f4a53"),
                "Дополнение (оценка уверенности): в JSON-ответ ВСЕГДА добавляй поля \"confidence\" — число от 0 до 1, " +
                "твоя уверенность, что ответ верен и подтверждён сниппетами (снижай, если сниппеты неполны, противоречат " +
                "друг другу или относятся к другому препарату/форме выпуска), и \"confidenceReason\" — одна короткая " +
                "фраза по-русски, почему такая оценка.",
                "Добавлены обязательные поля confidence/confidenceReason (ADR-0018, этап результата)."),
            ("lab-analyte.summarize", new Guid("3e0a7c11-52d4-4b8e-9a6f-0c1d2e3f4a54"),
                "Дополнение (оценка уверенности): в JSON-ответ ВСЕГДА добавляй поля \"confidence\" — число от 0 до 1, " +
                "твоя уверенность, что ответ верен и полностью подтверждён сниппетами (снижай, если сниппеты противоречат " +
                "друг другу, неполны, нормы взяты не для того биоматериала или единицы неясны), и \"confidenceReason\" — " +
                "одна короткая фраза по-русски, почему такая оценка.",
                "Добавлены обязательные поля confidence/confidenceReason (ADR-0018, этап результата)."),
        ];

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

            migrationBuilder.AddColumn<string>(
                name: "DraftPayloadJson",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedQueryText",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "QueryConfidence",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QueryConfidenceReason",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ResultConfidence",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultConfidenceReason",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAt",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedByUserId",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SearchApprovedAt",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftPayloadJson",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedQueryText",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "QueryConfidence",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QueryConfidenceReason",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ResultConfidence",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultConfidenceReason",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAt",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedByUserId",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SearchApprovedAt",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftPayloadJson",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedQueryText",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "QueryConfidence",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QueryConfidenceReason",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ResultConfidence",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultConfidenceReason",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAt",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedByUserId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SearchApprovedAt",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EnrichmentReviewConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MedicationQueryMinConfidence = table.Column<double>(type: "double precision", nullable: false),
                    AnalyteQueryMinConfidence = table.Column<double>(type: "double precision", nullable: false),
                    MedicationResultMinConfidence = table.Column<double>(type: "double precision", nullable: false),
                    AnalyteResultMinConfidence = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrichmentReviewConfigs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisitMedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                column: "NormalizedName",
                unique: true,
                filter: "\"Status\" IN (0, 1, 5, 6, 7)");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                column: "NormalizedName",
                unique: true,
                filter: "\"Status\" IN (0, 1, 5, 6, 7)");

            migrationBuilder.CreateIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_SpecimenKbId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                columns: new[] { "NormalizedName", "SpecimenKbId" },
                unique: true,
                filter: "\"Status\" IN (0, 1, 5, 6, 7)");

            // Новые активные версии промптов. Один оператор на ключ: частичный уникальный индекс
            // допускает одну активную версию на промпт, поэтому старая гасится и отдаёт тело через
            // RETURNING, а новая вставляется из него. Нет активной версии (слот не засеян) — no-op.
            foreach (var (key, versionId, addendum, note) in ConfidencePrompts)
            {
                migrationBuilder.Sql($"""
                    WITH p AS (SELECT "Id" FROM "PipelinePrompts" WHERE "Key" = '{key}'),
                    old AS (
                        UPDATE "PipelinePromptVersions" v SET "IsActive" = FALSE
                        FROM p WHERE v."PromptId" = p."Id" AND v."IsActive" = TRUE
                        RETURNING v."PromptId", v."Body"
                    )
                    INSERT INTO "PipelinePromptVersions" ("Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt")
                    SELECT '{versionId}', old."PromptId",
                           (SELECT COALESCE(MAX("Version"), 0) + 1 FROM "PipelinePromptVersions" WHERE "PromptId" = old."PromptId"),
                           old."Body" || E'\n\n' || '{Esc(addendum)}', TRUE, '{Esc(note)}', '{SeedCreatedAt:O}'
                    FROM old LIMIT 1;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (key, versionId, _, _) in ConfidencePrompts)
            {
                migrationBuilder.Sql($"""
                    DELETE FROM "PipelinePromptVersions" WHERE "Id" = '{versionId}';
                    """);
                migrationBuilder.Sql($"""
                    UPDATE "PipelinePromptVersions" SET "IsActive" = TRUE
                    WHERE "Version" = (
                        SELECT MAX(v2."Version") FROM "PipelinePromptVersions" v2
                        WHERE v2."PromptId" = "PipelinePromptVersions"."PromptId")
                      AND "PromptId" IN (SELECT "Id" FROM "PipelinePrompts" WHERE "Key" = '{key}')
                      AND NOT EXISTS (
                        SELECT 1 FROM "PipelinePromptVersions" a
                        WHERE a."PromptId" = "PipelinePromptVersions"."PromptId" AND a."IsActive");
                    """);
            }

            migrationBuilder.DropTable(
                name: "EnrichmentReviewConfigs");

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

            migrationBuilder.DropColumn(
                name: "DraftPayloadJson",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ProposedQueryText",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "QueryConfidence",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "QueryConfidenceReason",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ResultConfidence",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ResultConfidenceReason",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ReviewedByUserId",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "SearchApprovedAt",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "DraftPayloadJson",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ProposedQueryText",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "QueryConfidence",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "QueryConfidenceReason",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ResultConfidence",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ResultConfidenceReason",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ReviewedByUserId",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "SearchApprovedAt",
                schema: "medical",
                table: "MedicationEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "DraftPayloadJson",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ProposedQueryText",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "QueryConfidence",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "QueryConfidenceReason",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ResultConfidence",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ResultConfidenceReason",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "ReviewedByUserId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "SearchApprovedAt",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

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

        private static string Esc(string value) => value.Replace("'", "''");
    }
}
