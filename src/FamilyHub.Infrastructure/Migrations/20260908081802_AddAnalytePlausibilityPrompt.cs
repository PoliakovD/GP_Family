using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Сид версии 1 (активной) для слота "analysis.analyte-plausibility"
    // (AnalytePlausibilityGuardService) — гейт "на бред" для показателей, введённых пользователем
    // вручную (EnrichmentRequestOrigin.ManualEntry). Текст скопирован ВЕРБАТИМ из
    // AnalytePlausibilityGuardService.FallbackPrompt — тот же приём, что AddLegitimacyGuardPrompt.
    public partial class AddAnalytePlausibilityPrompt : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);

        private const string AnalytePlausibilityBody = """
            Ты — валидатор правдоподобности лабораторного показателя, введённого пользователем вручную
            (не распознанного с бланка). На входе — название показателя анализа и, если известен,
            источник/биоматериал, из которого он получен. Оцени: (1) является ли название реально
            существующим лабораторным или клиническим показателем (а не случайным набором слов,
            выдумкой или посторонним текстом), и (2) если источник указан — имеет ли смысл измерять
            ИМЕННО этот показатель именно в этом источнике. Верни ТОЛЬКО валидный JSON, без пояснений,
            без markdown, без блока <think>.

            Формат ответа: {"valid": true, "reason": null}

            Правила:
            - "valid": false — если название не является реально существующим лабораторным/клиническим
              показателем (случайный текст, выдумка, название препарата или источника вместо
              показателя, оскорбление, посторонний контент), ЛИБО если указанное сочетание
              показатель+источник абсурдно (показатель физически не может быть получен из названного
              источника).
            - "valid": true — реальный, пусть даже редкий или узкоспециализированный показатель;
              сомнение в редком, но существующем названии трактуй В ПОЛЬЗУ валидности. Если источник
              не указан (null) — оценивай только само название показателя, без пункта (2).
            - "reason" — короткая причина отказа по-русски при valid=false, иначе null.
            - Верни строго один JSON-объект, ничего кроме него.
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var promptId = new Guid("2d3e4f50-6172-4839-9a0b-1c2d3e4f5a6b");

            migrationBuilder.InsertData(
                table: "PipelinePrompts",
                columns: new[] { "Id", "Key", "Description", "CreatedAt" },
                values: new object[]
                {
                    promptId, "analysis.analyte-plausibility",
                    "Гейт «на бред» для показателей, введённых вручную — реальность названия и осмысленность сочетания с источником (см. AnalytePlausibilityGuardService).",
                    SeedCreatedAt,
                });

            migrationBuilder.InsertData(
                table: "PipelinePromptVersions",
                columns: new[] { "Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt" },
                values: new object[]
                {
                    new Guid("3e4f5061-7283-49a0-8b1c-2d3e4f5a6b7c"), promptId, 1, AnalytePlausibilityBody, true, null, SeedCreatedAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PipelinePromptVersions",
                keyColumn: "Id",
                keyValue: new Guid("3e4f5061-7283-49a0-8b1c-2d3e4f5a6b7c"));

            migrationBuilder.DeleteData(
                table: "PipelinePrompts",
                keyColumn: "Id",
                keyValue: new Guid("2d3e4f50-6172-4839-9a0b-1c2d3e4f5a6b"));
        }
    }
}
