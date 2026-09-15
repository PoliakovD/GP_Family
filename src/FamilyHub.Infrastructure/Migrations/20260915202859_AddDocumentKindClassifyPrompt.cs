using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Сид версии 1 (активной) для слота "document.kind-classify" (DocumentKindClassifier) —
    // необязательный шаг батч-загрузки (см. PipelineCatalog, AnalysisExtraction/"kind-classify").
    // Текст скопирован ВЕРБАТИМ из DocumentKindClassifier.SystemPrompt — тот же приём, что
    // AddLegitimacyGuardPrompt/AddPipelineConfig.
    public partial class AddDocumentKindClassifyPrompt : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);

        private const string KindClassifyBody = """
            Ты — классификатор типа медицинского документа. На входе — текст (может быть частью
            документа) или фото. Определи, это БЛАНК ЛАБОРАТОРНОГО АНАЛИЗА (таблица показателей со
            значениями/единицами измерения/референсами — общий анализ крови, биохимия, ПЦР, УЗИ с
            числовыми параметрами и т.п.) или ЗАКЛЮЧЕНИЕ/ВЫПИСКА ВРАЧА (текст приёма — диагноз, анамнез,
            рекомендации, назначения, без таблицы показателей с числовыми значениями). Верни ТОЛЬКО
            валидный JSON, без пояснений, без markdown, без блока <think>.

            Формат ответа:
            {"kind": "analysis" или "visit", "confidence": 0.0, "reason": "короткое обоснование по-русски"}

            Правила:
            - "kind": "analysis" — документ ПРЕИМУЩЕСТВЕННО таблица показателей (даже если в шапке
              есть направившим врач и краткий комментарий) — таблица со значениями решает.
            - "kind": "visit" — документ ПРЕИМУЩЕСТВЕННО текст: диагноз, жалобы, осмотр, рекомендации,
              назначенные препараты — без таблицы числовых показателей с референсами.
            - "confidence" — число от 0 до 1, твоя уверенность именно в выборе между этими двумя видами.
            - Если документ смешанный или неопределимый — выбери вид, который преобладает по объёму, и
              понизь confidence соответственно.
            - Верни строго один JSON-объект, ничего кроме него.
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var promptId = new Guid("a0032a7b-d3e5-478b-9fb5-884387f74caa");

            migrationBuilder.InsertData(
                table: "PipelinePrompts",
                columns: new[] { "Id", "Key", "Description", "CreatedAt" },
                values: new object[]
                {
                    promptId, "document.kind-classify",
                    "Определение вида документа (анализ/посещение врача) для батч-загрузки, где пользователь не выбирает вид явно (см. DocumentKindClassifier).",
                    SeedCreatedAt,
                });

            migrationBuilder.InsertData(
                table: "PipelinePromptVersions",
                columns: new[] { "Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt" },
                values: new object[]
                {
                    new Guid("066810cc-3fe1-4bc8-8d61-a4b3c3048c12"), promptId, 1, KindClassifyBody, true, null, SeedCreatedAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PipelinePromptVersions",
                keyColumn: "Id",
                keyValue: new Guid("066810cc-3fe1-4bc8-8d61-a4b3c3048c12"));

            migrationBuilder.DeleteData(
                table: "PipelinePrompts",
                keyColumn: "Id",
                keyValue: new Guid("a0032a7b-d3e5-478b-9fb5-884387f74caa"));
        }
    }
}
