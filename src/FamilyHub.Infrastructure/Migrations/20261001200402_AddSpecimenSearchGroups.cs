using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecimenSearchGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_lab_analyte_search_cache_NormalizedName_SpecimenKbId",
                schema: "kb",
                table: "lab_analyte_search_cache");

            migrationBuilder.AddColumn<string>(
                name: "SearchGroupKey",
                schema: "kb",
                table: "lab_analyte_search_cache",
                type: "character varying(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchGroupKey",
                schema: "kb",
                table: "global_specimens_kb",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // ADR-0018: группы поиска биоматериалов. Данные — между добавлением колонок и созданием уникального индекса.
            // 1) Прежнее поведение: ключ строки кэша — сам биоматериал.
            migrationBuilder.Sql("""
                UPDATE kb.lab_analyte_search_cache SET "SearchGroupKey" = 'specimen:' || "SpecimenKbId"::text
                """);

            // 2) Стартовая группа «кровь»: кровь / венозная кровь / плазма — один поисковый запрос «<показатель> (кровь)».
            //    Моча, кал, мазки и прочие — без группы (каждый сам по себе). Дальше админ правит группы в админке.
            migrationBuilder.Sql("""
                UPDATE kb.global_specimens_kb SET "SearchGroupKey" = 'кровь'
                WHERE lower("DisplayName") IN ('кровь', 'венозная кровь', 'плазма')
                """);

            // 3) Строки кэша сгруппированных биоматериалов переезжают на ключ группы.
            migrationBuilder.Sql("""
                UPDATE kb.lab_analyte_search_cache c SET "SearchGroupKey" = 'group:' || s."SearchGroupKey"
                FROM kb.global_specimens_kb s
                WHERE s."Id" = c."SpecimenKbId" AND s."SearchGroupKey" IS NOT NULL
                """);

            // 4) Одноразовое слияние: внутри группы остаётся самая свежая строка по каждому названию (override'ы
            //    проигравших строк не переносятся — до этой миграции ручных сниппетов не существовало, а override'ы
            //    относились к URL, которых в победившей строке может не быть).
            migrationBuilder.Sql("""
                DELETE FROM kb.lab_analyte_search_cache c
                USING (
                    SELECT "Id", row_number() OVER (
                        PARTITION BY "NormalizedName", "SearchGroupKey" ORDER BY "LastUpdatedAt" DESC, "Id") AS rn
                    FROM kb.lab_analyte_search_cache
                ) d
                WHERE c."Id" = d."Id" AND d.rn > 1
                """);

            migrationBuilder.CreateIndex(
                name: "IX_lab_analyte_search_cache_NormalizedName_SearchGroupKey",
                schema: "kb",
                table: "lab_analyte_search_cache",
                columns: new[] { "NormalizedName", "SearchGroupKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_lab_analyte_search_cache_NormalizedName_SearchGroupKey",
                schema: "kb",
                table: "lab_analyte_search_cache");

            migrationBuilder.DropColumn(
                name: "SearchGroupKey",
                schema: "kb",
                table: "lab_analyte_search_cache");

            migrationBuilder.DropColumn(
                name: "SearchGroupKey",
                schema: "kb",
                table: "global_specimens_kb");

            migrationBuilder.CreateIndex(
                name: "IX_lab_analyte_search_cache_NormalizedName_SpecimenKbId",
                schema: "kb",
                table: "lab_analyte_search_cache",
                columns: new[] { "NormalizedName", "SpecimenKbId" },
                unique: true);
        }
    }
}
