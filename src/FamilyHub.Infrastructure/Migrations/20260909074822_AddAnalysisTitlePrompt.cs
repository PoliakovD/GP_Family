using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Заметка 4: короткое название анализа переезжает из побочного поля "suggestedTitle" внутри
    // analysis.extract в отдельный LLM-проход (analysis.title, см. AnalysisTitleGenerator) — тот
    // же приём, что уже применён к специмену (SpecimenResolver). Сеет новый слот analysis.title
    // (v1) и новую активную версию analysis.extract (v2) без "suggestedTitle" в схеме ответа.
    public partial class AddAnalysisTitlePrompt : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);

        private static readonly Guid AnalysisExtractPromptId = new("45508844-3607-4c68-b345-b34ca62d67f4");

        private const string AnalysisTitleBody = """
            Ты — специалист по коротким названиям медицинских анализов. На входе — шапка документа
            (может быть неполной или отсутствовать) и список показателей, которые реально удалось
            извлечь из этого бланка. Придумай КОРОТКОЕ (2-5 слов) литературное название анализа — то,
            как его обычно называют в направлении врача или в быту (например, "Общий анализ крови",
            "Биохимический анализ крови", "Общий анализ мочи", "Спермограмма", "Гормоны щитовидной
            железы"). Верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

            Формат ответа: {"title": "Общий анализ крови"}

            Правила:
            - Если название анализа напечатано в шапке документа прямо — используй его в литературном
              виде (без номера бланка/названия лаборатории/лишних слов).
            - Если в шапке названия нет — определи его по составу показателей (например, гемоглобин +
              эритроциты + лейкоциты → "Общий анализ крови").
            - Если по показателям тоже невозможно понять, что это за анализ — верни {"title": null},
              не придумывай название наугад.
            - Название должно быть коротким и узнаваемым — не пересказывай список показателей и не
              перечисляй их через запятую вместо названия.
            - Верни строго один JSON-объект, ничего кроме него.
            """;

        private const string AnalysisExtractBodyV2 = """
            Ты — оцифровщик бланков лабораторных анализов. На входе — текст или фото бланка анализа
            (может быть только часть бланка, если документ большой). Извлеки ВСЕ показатели, которые
            реально присутствуют в этом фрагменте, и верни ТОЛЬКО валидный JSON, без пояснений, без
            markdown, без блока <think>.

            Формат ответа:
            {
              "indicators": [
                {
                  "name": "название показателя БЕЗ порядкового номера пункта бланка и БЕЗ единицы измерения (например, \"Гемоглобин\", не \"1. Гемоглобин, г/л\")",
                  "value": "значение как напечатано (например, \"118\" или \"отрицательно\")",
                  "unit": "единица измерения или null (например, \"г/л\")",
                  "refLow": 130,
                  "refHigh": 160,
                  "refText": "референсный диапазон текстом или null — заполняй ТОЛЬКО если референс НЕ раскладывается на refLow/refHigh (например, \"отрицательно\", \"1-3 в п/зр\")"
                }
              ],
              "documentDate": "дата анализа/забора материала, как указана в бланке, в формате YYYY-MM-DD, или null",
              "doctor": "ФИО и/или специальность врача, назначившего анализ, если указаны в бланке — иначе null, не придумывай"
            }

            Правила:
            - Извлекай ТОЛЬКО то, что реально написано в этом фрагменте — ничего не добавляй от себя
              и не переноси показатели из общих знаний о медицине.
            - "name" — название показателя БЕЗ порядкового номера строки/пункта бланка ("1.", "12)" и
              т.п. в начале — это нумерация бланка, не часть названия) и без единицы измерения (она
              отдельным полем "unit"). Регистр — как обычно пишут в литературном тексте (с заглавной
              буквы), даже если в бланке весь текст напечатан КАПСОМ.
            - Если в строке бланка нет значения (пустая ячейка, только название показателя без цифры
              или текста напротив, ЛИБО там стоит только прочерк "-"/"—") — НЕ включай этот показатель
              в ответ вообще, пропусти его: прочерк ничего не говорит о результате анализа, хранить
              его бессмысленно. Если же в бланке явно написано СЛОВОМ "отсутствуют", "не обнаружено"
              или "отрицательно" — это осмысленный качественный РЕЗУЛЬТАТ анализа, а не пустая ячейка:
              включай показатель с ним как есть.
            - "refLow"/"refHigh" — числа, только если референс — числовой диапазон (например,
              "130-160"). Если так — "refText" оставь null. Если референс не числовой — заполни
              только "refText", "refLow"/"refHigh" оставь null.
            - "documentDate"/"doctor" — заполняй, только если это ДЕЙСТВИТЕЛЬНО есть в этом фрагменте
              (обычно в шапке документа); если фрагмент — просто таблица показателей без шапки,
              оставь оба null.
            - Если во фрагменте нет ни одного показателя анализа (это шапка документа, подпись врача,
              пояснительный текст и т.п.) — indicators пустой массив, но documentDate/doctor всё равно
              заполни, если они есть в этом фрагменте.
            - Верни строго один JSON-объект, ничего кроме него.
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var titlePromptId = new Guid("8c3d4e5f-6071-4293-abcd-2e3f4a5b6c73");

            migrationBuilder.InsertData(
                table: "PipelinePrompts",
                columns: new[] { "Id", "Key", "Description", "CreatedAt" },
                values: new object[]
                {
                    titlePromptId, "analysis.title",
                    "Короткое название анализа (заметка 4) — отдельный LLM-проход по шапке документа и уже извлечённым показателям, точнее поля внутри analysis.extract.",
                    SeedCreatedAt,
                });

            migrationBuilder.InsertData(
                table: "PipelinePromptVersions",
                columns: new[] { "Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt" },
                values: new object[]
                {
                    new Guid("9d4e5f60-7182-4394-bcde-3f4a5b6c7d84"), titlePromptId, 1, AnalysisTitleBody, true, null, SeedCreatedAt,
                });

            // analysis.extract — новая активная версия без "suggestedTitle" в схеме ответа (заметка 4).
            migrationBuilder.Sql($"""
                UPDATE "PipelinePromptVersions" SET "IsActive" = false
                WHERE "PromptId" = '{AnalysisExtractPromptId}' AND "IsActive" = true;
                """);

            migrationBuilder.InsertData(
                table: "PipelinePromptVersions",
                columns: new[] { "Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt" },
                values: new object[]
                {
                    new Guid("ae5f6071-8293-45a5-cdef-405b6c7d8e95"), AnalysisExtractPromptId, 2, AnalysisExtractBodyV2, true,
                    "\"suggestedTitle\" убран из схемы ответа — название анализа теперь отдельный шаг (analysis.title).",
                    SeedCreatedAt,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                UPDATE "PipelinePromptVersions" SET "IsActive" = true
                WHERE "PromptId" = '{AnalysisExtractPromptId}' AND "Version" = 1;
                """);

            migrationBuilder.DeleteData(
                table: "PipelinePromptVersions",
                keyColumn: "Id",
                keyValue: new Guid("ae5f6071-8293-45a5-cdef-405b6c7d8e95"));

            migrationBuilder.DeleteData(
                table: "PipelinePromptVersions",
                keyColumn: "Id",
                keyValue: new Guid("9d4e5f60-7182-4394-bcde-3f4a5b6c7d84"));

            migrationBuilder.DeleteData(
                table: "PipelinePrompts",
                keyColumn: "Id",
                keyValue: new Guid("8c3d4e5f-6071-4293-abcd-2e3f4a5b6c73"));
        }
    }
}
