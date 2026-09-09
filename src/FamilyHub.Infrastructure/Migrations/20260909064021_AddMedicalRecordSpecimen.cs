using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMedicalRecordSpecimen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpecimenHint",
                schema: "medical",
                table: "MedicalRecords",
                type: "text",
                nullable: true);

            // Дефолт — SpecimenContextIds.Unresolved (Domain: "00000000-...-000000000001"), НЕ
            // Guid.Empty — тот же сентинел, что уже используется на LabIndicator.SpecimenKbId.
            migrationBuilder.AddColumn<Guid>(
                name: "SpecimenKbId",
                schema: "medical",
                table: "MedicalRecords",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            // Бэкфилл существующих записей (заметка 1 — источник переезжает с показателя на
            // запись): для каждой записи берём САМЫЙ ЧАСТЫЙ резолвленный (не Unresolved)
            // SpecimenKbId среди её текущих показателей — до этой миграции один анализ мог нести
            // показатели с разными источниками (посекционный резолвинг, теперь убран), поэтому
            // берём большинство, а не первый попавшийся. Записи без единого резолвленного
            // источника среди показателей (например, только Unresolved) остаются на дефолте.
            migrationBuilder.Sql("""
                UPDATE medical."MedicalRecords" r
                SET "SpecimenKbId" = ranked."SpecimenKbId"
                FROM (
                    SELECT DISTINCT ON ("MedicalRecordId") "MedicalRecordId", "SpecimenKbId"
                    FROM (
                        SELECT "MedicalRecordId", "SpecimenKbId", COUNT(*) AS cnt
                        FROM medical."LabIndicators"
                        WHERE "SpecimenKbId" <> '00000000-0000-0000-0000-000000000001'
                        GROUP BY "MedicalRecordId", "SpecimenKbId"
                    ) counts
                    ORDER BY "MedicalRecordId", cnt DESC
                ) ranked
                WHERE r."Id" = ranked."MedicalRecordId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpecimenHint",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropColumn(
                name: "SpecimenKbId",
                schema: "medical",
                table: "MedicalRecords");
        }
    }
}
