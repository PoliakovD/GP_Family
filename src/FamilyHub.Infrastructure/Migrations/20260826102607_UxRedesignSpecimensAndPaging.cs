using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UxRedesignSpecimensAndPaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_MedicalRecordId_AnalyteKey_Specimen",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey_Specimen",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.AddColumn<Guid>(
                name: "SpecimenCustomId",
                schema: "medical",
                table: "LabIndicators",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserSpecimens",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSpecimens", x => x.Id);
                });

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
                name: "IX_UserSpecimens_OwnerUserId_NormalizedName",
                schema: "medical",
                table: "UserSpecimens",
                columns: new[] { "OwnerUserId", "NormalizedName" },
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LabIndicators_UserSpecimens_SpecimenCustomId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropTable(
                name: "UserSpecimens",
                schema: "medical");

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

            migrationBuilder.DropColumn(
                name: "SpecimenCustomId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_MedicalRecordId_AnalyteKey_Specimen",
                schema: "medical",
                table: "LabIndicators",
                columns: new[] { "MedicalRecordId", "AnalyteKey", "Specimen" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey_Specimen",
                schema: "medical",
                table: "LabIndicators",
                columns: new[] { "OwnerUserId", "AnalyteKey", "Specimen" });
        }
    }
}
