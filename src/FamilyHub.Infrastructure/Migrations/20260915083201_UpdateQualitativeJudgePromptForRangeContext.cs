using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Новая активная версия "analysis.qualitative-judge" (QualitativeNormJudge) — v1 уже могла
    // быть применена на живой БД (не редактируем сид на месте, см. паттерн AddReferenceRangePrompt),
    // поэтому — новая версия. Расширяет контекст, передаваемый модели: границы известного
    // референсного диапазона (если есть) + HighMeans/LowMeans статьи справочника РАЗДЕЛЬНО (не
    // одной строкой) — живой случай: отсутствие/следовое количество показателя ниже диапазона,
    // начинающегося не с нуля (например "2-10"), для многих показателей норма, а не отклонение;
    // раньше модель не видела ни диапазон, ни это пояснение. Версия — MAX(Version)+1 динамически,
    // не хардкод (см. AddReferenceRangePrompt).
    public partial class UpdateQualitativeJudgePromptForRangeContext : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);

        private static readonly Guid QualitativeJudgePromptId = new("6c7d8e90-a1b2-4c3d-8e9f-405b6c7d8e9f");

        private static readonly Guid NewVersionId = new("8e90a1b2-c3d4-4e5f-a0b1-627d8e9fa0b1");

        private const string QualitativeJudgeBodyV2 = """
            Ты — опытный врач лабораторной диагностики. На входе — название лабораторного показателя
            (часто содержит методику исследования), его результат и весь уже известный контекст
            (референсный диапазон, если он где-то есть; что означают повышенный/пониженный результат
            именно для этого показателя, если это известно). Определи, является ли результат
            НОРМАЛЬНЫМ (здоровым, не требующим внимания врача) для ЭТОГО конкретного показателя. Верни
            ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

            Формат ответа: {"isNormal": true, "confidence": 0.8}

            Правила:
            - "isNormal": true — результат в пределах ожидаемой нормы; false — явное отклонение,
              требующее внимания; null — ты не можешь уверенно определить это по данному контексту
              (недостаточно данных, редкий/неоднозначный показатель) — в этом случае НЕ угадывай,
              верни null.
            - "confidence" — число от 0 до 1, твоя уверенность в ответе (для null-ответа можно 0).
            - Если дан референсный диапазон — НЕ считай механически, что "значение вне диапазона" всегда
              означает отклонение. В частности, отсутствие/следовое количество показателя (значения
              вроде "не обнаружено", "отсутствует", "0") НИЖЕ нижней границы диапазона (например,
              диапазон "2-10", значение отсутствует) для МНОГИХ показателей — здоровая норма, а не
              патология: используй "Что означает пониженный результат" (если дано), чтобы понять,
              действительно ли это отклонение именно для этого показателя, или норма.
            - Обильность/количество ("++", "обильно", "умеренно", "скудно", "единичные") для многих
              качественных лабораторных результатов ЗНАЧИМА — единичные/скудные находки часто норма,
              обильные/множественные часто отклонение, но это зависит от конкретного показателя, суди
              по своим знаниям, не по универсальному правилу.
            - Если рядом дана "Подсказка" (более ранняя догадка модели о норме) — используй её как
              ориентир, но не следуй слепо, если она явно противоречит конкретному значению.
            - Верни строго один JSON-объект, ничего кроме него.
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                UPDATE "PipelinePromptVersions" SET "IsActive" = false
                WHERE "PromptId" = '{QualitativeJudgePromptId}' AND "IsActive" = true;
                """);

            migrationBuilder.Sql($"""
                INSERT INTO "PipelinePromptVersions" ("Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt")
                SELECT '{NewVersionId}', '{QualitativeJudgePromptId}', COALESCE(MAX("Version"), 0) + 1,
                       '{Escape(QualitativeJudgeBodyV2)}', TRUE,
                       'Модели теперь передаются границы известного референсного диапазона и HighMeans/LowMeans справочника раздельно — отсутствие/следовое количество ниже ненулевого диапазона (например "2-10") для многих показателей норма, а не отклонение.',
                       '{SeedCreatedAt:O}'
                FROM "PipelinePromptVersions" WHERE "PromptId" = '{QualitativeJudgePromptId}';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE FROM "PipelinePromptVersions" WHERE "Id" = '{NewVersionId}';
                """);

            migrationBuilder.Sql($"""
                UPDATE "PipelinePromptVersions" SET "IsActive" = true
                WHERE "PromptId" = '{QualitativeJudgePromptId}' AND "Version" = (
                    SELECT MAX("Version") FROM "PipelinePromptVersions" WHERE "PromptId" = '{QualitativeJudgePromptId}'
                );
                """);
        }

        /// <summary>Экранирование одиночной кавычки для интерполяции в сырой SQL — тот же приём,
        /// что AddReferenceRangePrompt.</summary>
        private static string Escape(string value) => value.Replace("'", "''");
    }
}
