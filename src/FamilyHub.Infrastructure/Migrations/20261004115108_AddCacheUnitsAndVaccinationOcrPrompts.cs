using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Сид версии 1 (активной) для двух слотов, которые код уже читает через IPromptProvider, но которых не было
    // в каталоге и в БД — в админке их нельзя было ни увидеть, ни править: "analysis.cache-units"
    // (LabAnalyteCacheUnitsBackfillJob) и "vaccination.certificate-ocr" (VaccinationCertificateOcrService).
    // Тексты скопированы ВЕРБАТИМ из фолбэков в коде — тот же приём, что AddAnalytePlausibilityPrompt.
    public partial class AddCacheUnitsAndVaccinationOcrPrompts : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);

        private static readonly Guid CacheUnitsPromptId = new("cc33b7f6-0c02-40a4-b0c5-f4f006d9a55d");
        private static readonly Guid CacheUnitsVersionId = new("3673fd4e-f1db-4112-ae2f-7b15ba821c5a");
        private static readonly Guid VaccinationPromptId = new("3271838f-b275-4ce4-a787-c5e122c66d1f");
        private static readonly Guid VaccinationVersionId = new("aeae1220-e338-4272-9b4b-fcaa72bd577f");

        private const string CacheUnitsBody = """
            Ты — помощник по лабораторным справочникам. На входе — название показателя и фрагменты веб-страниц
            (результаты поиска). Перечисли единицы измерения, в которых во фрагментах ДЕЙСТВИТЕЛЬНО приведены
            референсные значения или результаты этого показателя (например "г/л", "ммоль/л", "%", "мкмоль/л").
            Верни ТОЛЬКО валидный JSON без пояснений и markdown: {"units": ["г/л", "ммоль/л"]}.
            Правила: единицу пиши так, как она напечатана во фрагментах; не добавляй единицы из общих знаний,
            если их нет в тексте; если единиц нет — {"units": []}. Верни строго один JSON-объект.
            """;

        private const string VaccinationOcrBody = """
            Ты — оцифровщик сертификата профилактических прививок (форма 156/у-93 или аналог).
            Проанализируй все прикреплённые фотографии страниц ОДНОГО сертификата и верни ТОЛЬКО
            валидный JSON-объект, без пояснений, без markdown, без блока <think>.

            Формат ответа:
            { "items": [
                { "name": "Название прививки или инфекции, как написано в сертификате",
                  "vaccine": "Название вакцины/производитель, если указано, иначе null",
                  "date": "Дата в формате dd/MM/yyyy, либо MM/yyyy, либо yyyy — как удалось разобрать",
                  "confident": true }
            ] }

            Правила:
            - Одна запись сертификата (одна дата) — один элемент массива; для многодозных вакцин
              (БЦЖ, АКДС, полиомиелит и т.п.) каждая отдельная отметка — свой элемент, не объединяй дозы.
            - "confident": false, если почерк или качество фото не позволяют уверенно прочитать дату или название.
            - Если дата совсем не разборчива — верни null в "date", а не выдуманную дату.
            - Верни строго один JSON-объект, ничего кроме него.
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "PipelinePrompts",
                columns: new[] { "Id", "Key", "Description", "CreatedAt" },
                values: new object[,]
                {
                    {
                        CacheUnitsPromptId, "analysis.cache-units",
                        "Единицы измерения, для которых в сохранённой выдаче платного поиска есть нормы (см. LabAnalyteCacheUnitsBackfillJob).",
                        SeedCreatedAt,
                    },
                    {
                        VaccinationPromptId, "vaccination.certificate-ocr",
                        "Распознавание прививочного сертификата по фото (см. VaccinationCertificateOcrService).",
                        SeedCreatedAt,
                    },
                });

            migrationBuilder.InsertData(
                table: "PipelinePromptVersions",
                columns: new[] { "Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt" },
                values: new object[,]
                {
                    { CacheUnitsVersionId, CacheUnitsPromptId, 1, CacheUnitsBody, true, null, SeedCreatedAt },
                    { VaccinationVersionId, VaccinationPromptId, 1, VaccinationOcrBody, true, null, SeedCreatedAt },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(table: "PipelinePromptVersions", keyColumn: "Id", keyValue: CacheUnitsVersionId);
            migrationBuilder.DeleteData(table: "PipelinePromptVersions", keyColumn: "Id", keyValue: VaccinationVersionId);
            migrationBuilder.DeleteData(table: "PipelinePrompts", keyColumn: "Id", keyValue: CacheUnitsPromptId);
            migrationBuilder.DeleteData(table: "PipelinePrompts", keyColumn: "Id", keyValue: VaccinationPromptId);
        }
    }
}
