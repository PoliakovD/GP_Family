using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <summary>
    /// Этап 4 (AI-конвейер обогащения): торговые названия (Aliases) в справочнике, задачи
    /// конвейера (MedicationEnrichmentJobs). Aliases — Postgres text[], как search_vector НЕ
    /// заведён в EF-модель (нет кроссплатформенного Npgsql/SQLite-юнит-тесты маппинга для
    /// массивов) — управляется только raw SQL здесь и в KbLookupService/KbCatalogService/KbWriter.
    /// search_vector (AddFullTextSearch) намеренно НЕ трогаем: array_to_string()/массив-в-text —
    /// STABLE, не IMMUTABLE (Postgres: "generation expression is not immutable" при попытке
    /// включить Aliases в generated-колонку). Поиск по алиасам — отдельным условием
    /// "{q} = ANY(Aliases)" в запросах (SearchService/KbLookupService/KbCatalogService), не через
    /// tsvector.
    /// </summary>
    public partial class AddMedicationEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PayloadVersion",
                schema: "kb",
                table: "global_medications_kb",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                "ALTER TABLE kb.global_medications_kb ADD COLUMN \"Aliases\" text[] NOT NULL DEFAULT '{}';");

            migrationBuilder.Sql(
                "CREATE INDEX \"IX_global_medications_kb_Aliases\" ON kb.global_medications_kb USING GIN (\"Aliases\");");

            migrationBuilder.CreateTable(
                name: "MedicationEnrichmentJobs",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceDisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MedicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ExternalSearchAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    KbId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicationEnrichmentJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MedicationEnrichmentJobs_MedicationId",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                column: "MedicationId");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                column: "NormalizedName",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationEnrichmentJobs_Status_CreatedAt",
                schema: "medical",
                table: "MedicationEnrichmentJobs",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MedicationEnrichmentJobs",
                schema: "medical");

            migrationBuilder.Sql("DROP INDEX IF EXISTS kb.\"IX_global_medications_kb_Aliases\";");
            migrationBuilder.Sql("ALTER TABLE kb.global_medications_kb DROP COLUMN \"Aliases\";");

            migrationBuilder.DropColumn(
                name: "PayloadVersion",
                schema: "kb",
                table: "global_medications_kb");
        }
    }
}
