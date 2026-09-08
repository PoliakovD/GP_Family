using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLabIndicatorPatientIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey_SpecimenKbId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.AddColumn<Guid>(
                name: "FamilyDependentId",
                schema: "medical",
                table: "LabIndicators",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetUserId",
                schema: "medical",
                table: "LabIndicators",
                type: "uuid",
                nullable: true);

            // Бэкфилл существующих строк с идентичностью пациента родительской записи —
            // денормализация вслед за уже денормализованными RecordDate/OwnerUserId (см. class doc
            // LabIndicator.FamilyDependentId). Без бэкфилла все существующие показатели молча
            // считались бы "моими" (оба поля null), даже если реально были заведены для зависимого/
            // другого участника семьи — то есть ровно тот баг, который эта миграция чинит, продолжал
            // бы жить для уже накопленных данных.
            migrationBuilder.Sql("""
                UPDATE medical."LabIndicators" li
                SET "FamilyDependentId" = mr."FamilyDependentId", "TargetUserId" = mr."TargetUserId"
                FROM medical."MedicalRecords" mr
                WHERE li."MedicalRecordId" = mr."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_OwnerUserId_FamilyDependentId_TargetUserId_An~",
                schema: "medical",
                table: "LabIndicators",
                columns: new[] { "OwnerUserId", "FamilyDependentId", "TargetUserId", "AnalyteKey", "SpecimenKbId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_OwnerUserId_FamilyDependentId_TargetUserId_An~",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropColumn(
                name: "FamilyDependentId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropColumn(
                name: "TargetUserId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey_SpecimenKbId",
                schema: "medical",
                table: "LabIndicators",
                columns: new[] { "OwnerUserId", "AnalyteKey", "SpecimenKbId" });
        }
    }
}
