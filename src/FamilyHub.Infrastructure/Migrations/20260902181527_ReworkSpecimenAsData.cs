using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <summary>
    /// Пересборка enrich-пайплайна: источник показателя (биоматериал ИЛИ инструментальное
    /// исследование — ЭКГ/УЗИ и т.п.) перестаёт быть фиксированным C#-enum'ом (SpecimenType) со
    /// switch-классификацией в коде и становится обычными строками общего справочника
    /// (kb.global_specimens_kb) — LLM нормализует, код только сверяет по триграмме (см.
    /// SpecimenResolver/GlobalSpecimenKbService). LabIndicator/GlobalLabAnalyteKb/
    /// LabAnalyteEnrichmentJob/LabAnalyteSearchCache получают SpecimenKbId (Guid) вместо Specimen
    /// (int)+опционального SpecimenCustomId; UserSpecimens становится тонкой таблицей "недавно
    /// использованные этим пользователем источники" поверх общего справочника, а не второй копией
    /// написания.
    ///
    /// Порядок ниже критичен: новые колонки добавляются РАНЬШЕ, чем старые удаляются, чтобы между
    /// ними можно было прогнать SQL-бэкфилл по ещё живым старым значениям (Specimen/SpecimenCustomId/
    /// UserSpecimens.NormalizedName) — иначе данные для маппинга исчезли бы до того, как их
    /// понадобилось прочитать.
    /// </summary>
    public partial class ReworkSpecimenAsData : Migration
    {
        // Легаси-биоматериалы прежнего enum SpecimenType — заводятся как обычные засеянные строки
        // общего справочника (Source="seed"), не как классификация в коде: любой НОВЫЙ источник
        // (ЭКГ, УЗИ и т.п.) заводится точно так же через SpecimenResolver/GlobalSpecimenKbService,
        // без нового значения enum и без миграции.
        private const string UnresolvedId = "00000000-0000-0000-0000-000000000001"; // SpecimenContextIds.Unresolved
        private const string BloodId = "00000000-0000-0000-0000-000000000002";
        private const string UrineId = "00000000-0000-0000-0000-000000000003";
        private const string StoolId = "00000000-0000-0000-0000-000000000004";
        private const string VaginalSwabId = "00000000-0000-0000-0000-000000000005";
        private const string SalivaId = "00000000-0000-0000-0000-000000000006";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- 1. Новые колонки (nullable-safe заглушка Unresolved, бэкфилл ниже перезапишет) ---

            migrationBuilder.AddColumn<string>(
                name: "RawDisplayName",
                schema: "medical",
                table: "LabIndicators",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SpecimenKbId",
                schema: "medical",
                table: "LabIndicators",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid(UnresolvedId));

            migrationBuilder.AddColumn<Guid>(
                name: "SpecimenKbId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid(UnresolvedId));

            migrationBuilder.AddColumn<Guid>(
                name: "SpecimenKbId",
                schema: "kb",
                table: "lab_analyte_search_cache",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid(UnresolvedId));

            migrationBuilder.AddColumn<Guid>(
                name: "SpecimenKbId",
                schema: "kb",
                table: "global_lab_analytes_kb",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid(UnresolvedId));

            migrationBuilder.AddColumn<Guid>(
                name: "SpecimenKbId",
                schema: "medical",
                table: "UserSpecimens",
                type: "uuid",
                nullable: true); // временно, пока не забэкфиллено ниже — NOT NULL проставляется в самом конце

            // --- 2. Сентинел "не определено" + легаси-биоматериалы как обычные строки справочника ---

            migrationBuilder.Sql($"""
                INSERT INTO kb.global_specimens_kb ("Id", "NormalizedName", "DisplayName", "Source", "CreatedAt")
                VALUES
                    ('{UnresolvedId}'::uuid, 'не определено', 'Не определено', 'seed', now()),
                    ('{BloodId}'::uuid, 'кровь', 'Кровь', 'seed', now()),
                    ('{UrineId}'::uuid, 'моча', 'Моча', 'seed', now()),
                    ('{StoolId}'::uuid, 'кал', 'Кал', 'seed', now()),
                    ('{VaginalSwabId}'::uuid, 'вагинальный мазок', 'Вагинальный мазок', 'seed', now()),
                    ('{SalivaId}'::uuid, 'слюна', 'Слюна', 'seed', now())
                ON CONFLICT ("NormalizedName") DO NOTHING;
                """);

            // --- 3. Бэкфилл LabIndicators — старые Specimen(int)/SpecimenCustomId ещё живы ---

            migrationBuilder.Sql($"""
                UPDATE medical."LabIndicators" SET "SpecimenKbId" = CASE "Specimen"
                    WHEN 1 THEN '{BloodId}'::uuid
                    WHEN 2 THEN '{UrineId}'::uuid
                    WHEN 3 THEN '{StoolId}'::uuid
                    WHEN 4 THEN '{VaginalSwabId}'::uuid
                    WHEN 5 THEN '{SalivaId}'::uuid
                    ELSE '{UnresolvedId}'::uuid
                END;
                """);

            // Other(6) с привязкой к персональному UserSpecimens — регистрируем как обычные строки
            // общего справочника (реальный текст, введённый/провалидированный пользователем, ещё
            // жив в старых колонках UserSpecimens.NormalizedName/DisplayName) и перепривязываем.
            // Other(6) БЕЗ привязки — реального текста нигде не сохранено, честно остаётся Unresolved
            // (уже проставлено шагом выше), доступно для ручной перепривязки в UI.
            migrationBuilder.Sql("""
                INSERT INTO kb.global_specimens_kb ("Id", "NormalizedName", "DisplayName", "Source", "CreatedAt")
                SELECT DISTINCT ON (us."NormalizedName") gen_random_uuid(), us."NormalizedName", us."DisplayName", 'migrated', now()
                FROM medical."LabIndicators" li
                JOIN medical."UserSpecimens" us ON us."Id" = li."SpecimenCustomId"
                WHERE li."Specimen" = 6 AND li."SpecimenCustomId" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM kb.global_specimens_kb g WHERE g."NormalizedName" = us."NormalizedName");
                """);

            migrationBuilder.Sql("""
                UPDATE medical."LabIndicators" li
                SET "SpecimenKbId" = g."Id"
                FROM medical."UserSpecimens" us
                JOIN kb.global_specimens_kb g ON g."NormalizedName" = us."NormalizedName"
                WHERE li."SpecimenCustomId" = us."Id" AND li."Specimen" = 6;
                """);

            // --- 4. Бэкфилл трёх kb-таблиц — той же СХЕМОЙ enum→Guid; точность здесь не критична:
            //        все три пересобираются заново поверх кэша сниппетов (админский "Пересобрать
            //        справочник", см. план, §4.2) поверх уже верных LabIndicators.SpecimenKbId выше.

            migrationBuilder.Sql($"""
                UPDATE medical."LabAnalyteEnrichmentJobs" SET "SpecimenKbId" = CASE "Specimen"
                    WHEN 1 THEN '{BloodId}'::uuid
                    WHEN 2 THEN '{UrineId}'::uuid
                    WHEN 3 THEN '{StoolId}'::uuid
                    WHEN 4 THEN '{VaginalSwabId}'::uuid
                    WHEN 5 THEN '{SalivaId}'::uuid
                    ELSE '{UnresolvedId}'::uuid
                END;
                """);

            migrationBuilder.Sql($"""
                UPDATE kb.lab_analyte_search_cache SET "SpecimenKbId" = CASE "Specimen"
                    WHEN 1 THEN '{BloodId}'::uuid
                    WHEN 2 THEN '{UrineId}'::uuid
                    WHEN 3 THEN '{StoolId}'::uuid
                    WHEN 4 THEN '{VaginalSwabId}'::uuid
                    WHEN 5 THEN '{SalivaId}'::uuid
                    ELSE '{UnresolvedId}'::uuid
                END;
                """);

            migrationBuilder.Sql($"""
                UPDATE kb.global_lab_analytes_kb SET "SpecimenKbId" = CASE "Specimen"
                    WHEN 1 THEN '{BloodId}'::uuid
                    WHEN 2 THEN '{UrineId}'::uuid
                    WHEN 3 THEN '{StoolId}'::uuid
                    WHEN 4 THEN '{VaginalSwabId}'::uuid
                    WHEN 5 THEN '{SalivaId}'::uuid
                    ELSE '{UnresolvedId}'::uuid
                END;
                """);

            // --- 5. Бэкфилл UserSpecimens — сопоставляем оставшиеся строки (не обязательно
            //        связанные с Other-показателем выше) с общим справочником по NormalizedName,
            //        заводя для них глобальную запись, если такой ещё нет.

            migrationBuilder.Sql("""
                INSERT INTO kb.global_specimens_kb ("Id", "NormalizedName", "DisplayName", "Source", "CreatedAt")
                SELECT DISTINCT ON (us."NormalizedName") gen_random_uuid(), us."NormalizedName", us."DisplayName", 'migrated', now()
                FROM medical."UserSpecimens" us
                WHERE NOT EXISTS (SELECT 1 FROM kb.global_specimens_kb g WHERE g."NormalizedName" = us."NormalizedName");
                """);

            migrationBuilder.Sql("""
                UPDATE medical."UserSpecimens" us
                SET "SpecimenKbId" = g."Id"
                FROM kb.global_specimens_kb g
                WHERE g."NormalizedName" = us."NormalizedName";
                """);

            // --- 6. Старые FK/индексы/колонки — теперь безопасно удалить, бэкфилл выше уже прочитал всё нужное ---

            migrationBuilder.DropForeignKey(
                name: "FK_LabIndicators_UserSpecimens_SpecimenCustomId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropIndex(
                name: "IX_UserSpecimens_OwnerUserId_NormalizedName",
                schema: "medical",
                table: "UserSpecimens");

            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_MedicalRecordId_AnalyteKey_Specimen_SpecimenC~",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey_Specimen_SpecimenCusto~",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_SpecimenCustomId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_Specimen",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_lab_analyte_search_cache_NormalizedName_Specimen",
                schema: "kb",
                table: "lab_analyte_search_cache");

            migrationBuilder.DropIndex(
                name: "IX_global_lab_analytes_kb_NormalizedName_Specimen",
                schema: "kb",
                table: "global_lab_analytes_kb");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                schema: "medical",
                table: "UserSpecimens");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                schema: "medical",
                table: "UserSpecimens");

            migrationBuilder.DropColumn(
                name: "Specimen",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropColumn(
                name: "SpecimenCustomId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropColumn(
                name: "Specimen",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "Specimen",
                schema: "kb",
                table: "lab_analyte_search_cache");

            migrationBuilder.DropColumn(
                name: "Specimen",
                schema: "kb",
                table: "global_lab_analytes_kb");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "medical",
                table: "UserSpecimens",
                newName: "LastUsedAt");

            // --- 7. UserSpecimens.SpecimenKbId — теперь забэкфиллено, включаем NOT NULL ---

            migrationBuilder.AlterColumn<Guid>(
                name: "SpecimenKbId",
                schema: "medical",
                table: "UserSpecimens",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            // --- 8. Новые индексы ---

            migrationBuilder.CreateIndex(
                name: "IX_UserSpecimens_OwnerUserId_LastUsedAt",
                schema: "medical",
                table: "UserSpecimens",
                columns: new[] { "OwnerUserId", "LastUsedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSpecimens_OwnerUserId_SpecimenKbId",
                schema: "medical",
                table: "UserSpecimens",
                columns: new[] { "OwnerUserId", "SpecimenKbId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_MedicalRecordId_AnalyteKey_SpecimenKbId",
                schema: "medical",
                table: "LabIndicators",
                columns: new[] { "MedicalRecordId", "AnalyteKey", "SpecimenKbId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey_SpecimenKbId",
                schema: "medical",
                table: "LabIndicators",
                columns: new[] { "OwnerUserId", "AnalyteKey", "SpecimenKbId" });

            migrationBuilder.CreateIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_SpecimenKbId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                columns: new[] { "NormalizedName", "SpecimenKbId" },
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_lab_analyte_search_cache_NormalizedName_SpecimenKbId",
                schema: "kb",
                table: "lab_analyte_search_cache",
                columns: new[] { "NormalizedName", "SpecimenKbId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_global_lab_analytes_kb_NormalizedName_SpecimenKbId",
                schema: "kb",
                table: "global_lab_analytes_kb",
                columns: new[] { "NormalizedName", "SpecimenKbId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Схемный откат — без восстановления данных (SpecimenKbId → enum потребовал бы
            // обратного маппинга, которого для новых, заведённых после миграции источников не
            // существует по построению). Соответствует конвенции проекта для необратимых
            // структурных миграций.
            migrationBuilder.DropIndex(
                name: "IX_UserSpecimens_OwnerUserId_LastUsedAt",
                schema: "medical",
                table: "UserSpecimens");

            migrationBuilder.DropIndex(
                name: "IX_UserSpecimens_OwnerUserId_SpecimenKbId",
                schema: "medical",
                table: "UserSpecimens");

            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_MedicalRecordId_AnalyteKey_SpecimenKbId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey_SpecimenKbId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_SpecimenKbId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_lab_analyte_search_cache_NormalizedName_SpecimenKbId",
                schema: "kb",
                table: "lab_analyte_search_cache");

            migrationBuilder.DropIndex(
                name: "IX_global_lab_analytes_kb_NormalizedName_SpecimenKbId",
                schema: "kb",
                table: "global_lab_analytes_kb");

            migrationBuilder.DropColumn(
                name: "SpecimenKbId",
                schema: "medical",
                table: "UserSpecimens");

            migrationBuilder.DropColumn(
                name: "RawDisplayName",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropColumn(
                name: "SpecimenKbId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropColumn(
                name: "SpecimenKbId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "SpecimenKbId",
                schema: "kb",
                table: "lab_analyte_search_cache");

            migrationBuilder.DropColumn(
                name: "SpecimenKbId",
                schema: "kb",
                table: "global_lab_analytes_kb");

            migrationBuilder.Sql($"DELETE FROM kb.global_specimens_kb WHERE \"Source\" IN ('seed', 'migrated');");

            migrationBuilder.RenameColumn(
                name: "LastUsedAt",
                schema: "medical",
                table: "UserSpecimens",
                newName: "CreatedAt");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                schema: "medical",
                table: "UserSpecimens",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                schema: "medical",
                table: "UserSpecimens",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Specimen",
                schema: "medical",
                table: "LabIndicators",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SpecimenCustomId",
                schema: "medical",
                table: "LabIndicators",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Specimen",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Specimen",
                schema: "kb",
                table: "lab_analyte_search_cache",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Specimen",
                schema: "kb",
                table: "global_lab_analytes_kb",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_UserSpecimens_OwnerUserId_NormalizedName",
                schema: "medical",
                table: "UserSpecimens",
                columns: new[] { "OwnerUserId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_MedicalRecordId_AnalyteKey_Specimen_SpecimenC~",
                schema: "medical",
                table: "LabIndicators",
                columns: new[] { "MedicalRecordId", "AnalyteKey", "Specimen", "SpecimenCustomId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey_Specimen_SpecimenCusto~",
                schema: "medical",
                table: "LabIndicators",
                columns: new[] { "OwnerUserId", "AnalyteKey", "Specimen", "SpecimenCustomId" });

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_SpecimenCustomId",
                schema: "medical",
                table: "LabIndicators",
                column: "SpecimenCustomId");

            migrationBuilder.CreateIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName_Specimen",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                columns: new[] { "NormalizedName", "Specimen" },
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_lab_analyte_search_cache_NormalizedName_Specimen",
                schema: "kb",
                table: "lab_analyte_search_cache",
                columns: new[] { "NormalizedName", "Specimen" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_global_lab_analytes_kb_NormalizedName_Specimen",
                schema: "kb",
                table: "global_lab_analytes_kb",
                columns: new[] { "NormalizedName", "Specimen" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LabIndicators_UserSpecimens_SpecimenCustomId",
                schema: "medical",
                table: "LabIndicators",
                column: "SpecimenCustomId",
                principalSchema: "medical",
                principalTable: "UserSpecimens",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
