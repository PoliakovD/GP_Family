using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Сид версии 1 (активной) для трёх новых слотов PromptCatalog.Prompts — шаблонов поисковых
    // запросов во внешний поиск (§ "мне нужен контроль над промптами, которые улетают в search").
    // Текст скопирован ВЕРБАТИМ из текущих захардкоженных констант (AnalyteSearchQueryBuilder.
    // FallbackTemplate, BraveSearchProvider.MedicationFallbackTemplate,
    // YandexSearchProvider.MedicationFallbackTemplate) — тот же приём, что и AddPipelineConfig:
    // пустая БД после этой миграции ведёт себя идентично коду до неё.
    public partial class AddSearchQueryPrompts : Migration
    {
        private static readonly DateTime SeedCreatedAt = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);

        private const string AnalyteSearchQueryBody =
            "{name}{specimen} анализ норма референсные значения у мужчин и женщин по возрасту единицы измерения";

        private const string MedicationSearchQueryBraveBody = "{name} инструкция по применению";

        private const string MedicationSearchQueryYandexBody =
            "{name}: инструкция по применению, показания, форма выпуска, условия хранения, влияние на управление транспортом";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var analyteSearchQueryId = new Guid("6f6a1ff2-6a9e-4a3d-9c8e-9c9a2b6d0d3f");
            var medicationSearchQueryBraveId = new Guid("2e5c6f2a-2f7e-4e0e-a3d3-3ab6b7b93b1a");
            var medicationSearchQueryYandexId = new Guid("8a1b6c0e-6e4a-4f7d-8b6f-2f6f2f0f3d5a");

            migrationBuilder.InsertData(
                table: "PipelinePrompts",
                columns: new[] { "Id", "Key", "Description", "CreatedAt" },
                values: new object[,]
                {
                    { analyteSearchQueryId, "analysis.search-query", "Шаблон поискового запроса для показателя анализа (Brave и Yandex). Плейсхолдеры: {name} — нормализованное название показателя, {specimen} — источник в скобках вида « (кровь)» либо пустая строка, если источник не определён.", SeedCreatedAt },
                    { medicationSearchQueryBraveId, "medication.search-query.brave", "Шаблон поискового запроса для медикамента в Brave (обычный ключевой поиск). Плейсхолдер: {name} — нормализованное название препарата.", SeedCreatedAt },
                    { medicationSearchQueryYandexId, "medication.search-query.yandex", "Шаблон поискового запроса для медикамента в Yandex GenSearch (развёрнутый вопрос, не ключевые слова). Плейсхолдер: {name} — нормализованное название препарата.", SeedCreatedAt },
                });

            migrationBuilder.InsertData(
                table: "PipelinePromptVersions",
                columns: new[] { "Id", "PromptId", "Version", "Body", "IsActive", "Note", "CreatedAt" },
                values: new object[,]
                {
                    { new Guid("3d0a6f8e-5f6a-4b0e-9f0a-6a7b8c9d0e1f"), analyteSearchQueryId, 1, AnalyteSearchQueryBody, true, null, SeedCreatedAt },
                    { new Guid("4e1b7f9f-6a7b-4c1f-8a1b-7b8c9d0e1f2a"), medicationSearchQueryBraveId, 1, MedicationSearchQueryBraveBody, true, null, SeedCreatedAt },
                    { new Guid("5f2c8a0a-7b8c-4d2a-9b2c-8c9d0e1f2a3b"), medicationSearchQueryYandexId, 1, MedicationSearchQueryYandexBody, true, null, SeedCreatedAt },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PipelinePromptVersions",
                keyColumn: "Id",
                keyValues: new object[]
                {
                    new Guid("3d0a6f8e-5f6a-4b0e-9f0a-6a7b8c9d0e1f"),
                    new Guid("4e1b7f9f-6a7b-4c1f-8a1b-7b8c9d0e1f2a"),
                    new Guid("5f2c8a0a-7b8c-4d2a-9b2c-8c9d0e1f2a3b"),
                });

            migrationBuilder.DeleteData(
                table: "PipelinePrompts",
                keyColumn: "Id",
                keyValues: new object[]
                {
                    new Guid("6f6a1ff2-6a9e-4a3d-9c8e-9c9a2b6d0d3f"),
                    new Guid("2e5c6f2a-2f7e-4e0e-a3d3-3ab6b7b93b1a"),
                    new Guid("8a1b6c0e-6e4a-4f7d-8b6f-2f6f2f0f3d5a"),
                });
        }
    }
}
