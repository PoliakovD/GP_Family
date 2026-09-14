using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Сид версии 1 (активной) для слота "analysis.qualitative-judge" (QualitativeNormJudge) —
    // последний, самый дорогой резервный шаг каскада расчёта нормы (RefSource.Inferred): когда
    // референса в бланке нет вовсе, а результат — описательный/качественный текст, не
    // раскладывающийся ни диапазоном, ни полярностью "обнаружено/не обнаружено" (шкалы обильности
    // "+"/"++"/"+++", развёрнутые описания находок мазка вроде "коккобацилярная флора, обильно").
    // Текст скопирован ВЕРБАТИМ из QualitativeNormJudge.SystemPrompt — тот же приём, что
    // AddAnalyteSubjectPrompt/AddLegitimacyGuardPrompt.
    public partial class AddQualitativeJudgePrompt : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

        private const string QualitativeJudgeBody = """
            Ты — опытный врач лабораторной диагностики. На входе — название лабораторного показателя
            (часто содержит методику исследования) и его результат. Референсный диапазон для этого
            показателя в бланке анализа НЕ напечатан, а результат — описательный или качественный, не
            раскладывается на простое "обнаружено"/"не обнаружено" (например, шкала обильности
            "+"/"++"/"+++", развёрнутое словесное описание вроде "кокки, обильно" в мазке). Определи,
            является ли результат НОРМАЛЬНЫМ (здоровым, не требующим внимания врача) для ЭТОГО
            конкретного показателя, по общемедицинским знаниям. Верни ТОЛЬКО валидный JSON, без
            пояснений, без markdown, без блока <think>.

            Формат ответа: {"isNormal": true, "confidence": 0.8}

            Правила:
            - "isNormal": true — результат в пределах ожидаемой нормы; false — явное отклонение,
              требующее внимания; null — ты не можешь уверенно определить это по названию и значению
              (недостаточно контекста, редкий/неоднозначный показатель) — в этом случае НЕ угадывай,
              верни null.
            - "confidence" — число от 0 до 1, твоя уверенность в ответе (для null-ответа можно 0).
            - Обильность/количество ("++", "обильно", "умеренно", "скудно", "единичные") для многих
              качественных лабораторных результатов ЗНАЧИМА — единичные/скудные находки часто норма,
              обильные/множественные часто отклонение, но это зависит от конкретного показателя, суди
              по своим знаниям, не по универсальному правилу.
            - Если рядом дана "Подсказка" (пояснение из справочника или более ранняя догадка модели о
              норме) — используй её как ориентир, но не следуй слепо, если она явно противоречит
              конкретному значению.
            - Верни строго один JSON-объект, ничего кроме него.
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var promptId = new Guid("6c7d8e90-a1b2-4c3d-8e9f-405b6c7d8e9f");

            migrationBuilder.InsertData(
                table: "PipelinePrompts",
                columns: new[] { "Id", "Key", "Description", "CreatedAt" },
                values: new object[]
                {
                    promptId, "analysis.qualitative-judge",
                    "Оценка нормы по смыслу свободного текста, когда референса в бланке нет, а результат не раскладывается ни диапазоном, ни полярностью «обнаружено/не обнаружено» (шкалы обильности, описательные находки мазков) — самый дорогой и самый редкий резервный шаг каскада.",
                    SeedCreatedAt,
                });

            migrationBuilder.InsertData(
                table: "PipelinePromptVersions",
                columns: new[] { "Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt" },
                values: new object[]
                {
                    new Guid("7d8e90a1-b2c3-4d4e-9f0a-516c7d8e9fa0"), promptId, 1, QualitativeJudgeBody, true, null, SeedCreatedAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PipelinePromptVersions",
                keyColumn: "Id",
                keyValue: new Guid("7d8e90a1-b2c3-4d4e-9f0a-516c7d8e9fa0"));

            migrationBuilder.DeleteData(
                table: "PipelinePrompts",
                keyColumn: "Id",
                keyValue: new Guid("6c7d8e90-a1b2-4c3d-8e9f-405b6c7d8e9f"));
        }
    }
}
