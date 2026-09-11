using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Сид версии 1 (активной) для слота "analysis.subject-resolve" (AnalyteSubjectResolver) —
    // уточнение родового названия показателя ("Бактериальные микроорганизмы") по конкретному
    // объекту исследования, названному отдельно в документе (обычно в разделе "Оказанные услуги") —
    // без этого несколько файлов посева на разные микроорганизмы в одной записи схлопывались бы в
    // один показатель по ключу дедупликации. Текст скопирован ВЕРБАТИМ из
    // AnalyteSubjectResolver.SystemPrompt — тот же приём, что AddLegitimacyGuardPrompt/
    // AddAnalytePlausibilityPrompt.
    public partial class AddAnalyteSubjectPrompt : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);

        private const string AnalyteSubjectResolveBody = """
            Ты — уточнитель объекта лабораторного исследования. На входе — текст (или фото) бланка
            анализа и список показателей, которые уже извлечены из его таблицы результатов.

            Некоторые виды анализов (посев на конкретный микроорганизм, аллергопроба на конкретный
            аллерген, анализ на конкретное антитело или ген) печатают в самой ТАБЛИЦЕ результатов
            только РОДОВОЕ слово ("Бактериальные микроорганизмы", "Аллерген", "Антитела класса IgG") —
            а то, что искали КОНКРЕТНО, названо только в другом месте документа, обычно в разделе
            "Оказанные услуги", "Наименование исследования", "Назначенные исследования" или в названии
            самого направления/заказа. Твоя задача — найти это конкретное название, только если оно
            РЕАЛЬНО ЕСТЬ в документе где-то за пределами таблицы. Верни ТОЛЬКО валидный JSON, без
            пояснений, без markdown, без блока <think>.

            Формат ответа:
            {
              "subject": "конкретный объект исследования литературным названием (например, \"Сальмонеллы\", \"Клещ домашней пыли\") или null",
              "rawLabel": "как объект назван в документе — цитата или близкий пересказ, или null",
              "evidence": "короткая цитата из документа, где это написано, или null",
              "confidence": 0.0
            }

            Правила:
            - Заполняй "subject", ТОЛЬКО если весь документ посвящён ОДНОМУ конкретному объекту
              исследования — а показатели в таблице названы лишь родовым/обобщённым словом. Если
              документ — обычный многострочный анализ (общий анализ крови, биохимия, общий анализ
              мочи), где у каждого показателя УЖЕ есть своё конкретное название — "subject": null,
              "confidence": 0, даже если где-то в документе упоминаются отдельные вещества/клетки.
            - Не путай родовое слово таблицы ("Бактериальные микроорганизмы", "Аллерген", "Антитела")
              с конкретным названием самого исследования ("Посев на Salmonella spp.", "Определение
              IgE к клещу домашней пыли") — именно второе и есть "subject", первое — то, что нужно
              уточнить.
            - Если конкретное название есть только в общих словах ("бактериологическое исследование"
              без указания микроорганизма) — этого недостаточно, "subject": null.
            - "confidence" — число от 0 до 1, твоя уверенность именно в том, что это ОДИН конкретный
              объект и что ты нашёл его верное название. Не придумывай — если не уверен, ставь низкое
              значение (менее 0.5).
            - Верни строго один JSON-объект, ничего кроме него.
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var promptId = new Guid("4f506172-8394-4a5b-8c9d-2e3f4a5b6c7d");

            migrationBuilder.InsertData(
                table: "PipelinePrompts",
                columns: new[] { "Id", "Key", "Description", "CreatedAt" },
                values: new object[]
                {
                    promptId, "analysis.subject-resolve",
                    "Уточнение родового названия показателя по документу (посев на конкретный микроорганизм, аллерген и т.п.) — когда таблица результатов печатает только родовое слово, а объект исследования назван отдельно, обычно в разделе «Оказанные услуги».",
                    SeedCreatedAt,
                });

            migrationBuilder.InsertData(
                table: "PipelinePromptVersions",
                columns: new[] { "Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt" },
                values: new object[]
                {
                    new Guid("50617283-94a5-4b6c-9d0e-3f4a5b6c7d8e"), promptId, 1, AnalyteSubjectResolveBody, true, null, SeedCreatedAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PipelinePromptVersions",
                keyColumn: "Id",
                keyValue: new Guid("50617283-94a5-4b6c-9d0e-3f4a5b6c7d8e"));

            migrationBuilder.DeleteData(
                table: "PipelinePrompts",
                keyColumn: "Id",
                keyValue: new Guid("4f506172-8394-4a5b-8c9d-2e3f4a5b6c7d"));
        }
    }
}
