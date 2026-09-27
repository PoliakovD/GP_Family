using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVaccinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludeVaccinations",
                schema: "medical",
                table: "DoctorReports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "VaccinationCertificates",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FamilyDependentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: true),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaccinationCertificates", x => x.Id);
                    table.CheckConstraint("CK_VaccinationCertificates_OneSubject", "(\"SubjectUserId\" IS NULL) <> (\"FamilyDependentId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_VaccinationCertificates_FamilyDependents_FamilyDependentId",
                        column: x => x.FamilyDependentId,
                        principalSchema: "identity",
                        principalTable: "FamilyDependents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Vaccinations",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FamilyDependentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeriesCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DoseIndex = table.Column<int>(type: "integer", nullable: true),
                    CustomName = table.Column<string>(type: "text", nullable: true),
                    VaccineName = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    DatePrecision = table.Column<int>(type: "integer", nullable: true),
                    CertificateId = table.Column<Guid>(type: "uuid", nullable: true),
                    WellbeingCheckAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WellbeingCheckSent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vaccinations", x => x.Id);
                    table.CheckConstraint("CK_Vaccinations_OneSubject", "(\"SubjectUserId\" IS NULL) <> (\"FamilyDependentId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Vaccinations_FamilyDependents_FamilyDependentId",
                        column: x => x.FamilyDependentId,
                        principalSchema: "identity",
                        principalTable: "FamilyDependents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaccinationCertificates_FamilyDependentId",
                schema: "medical",
                table: "VaccinationCertificates",
                column: "FamilyDependentId");

            migrationBuilder.CreateIndex(
                name: "IX_VaccinationCertificates_SubjectUserId",
                schema: "medical",
                table: "VaccinationCertificates",
                column: "SubjectUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Vaccinations_CertificateId",
                schema: "medical",
                table: "Vaccinations",
                column: "CertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_Vaccinations_FamilyDependentId",
                schema: "medical",
                table: "Vaccinations",
                column: "FamilyDependentId");

            migrationBuilder.CreateIndex(
                name: "IX_Vaccinations_FamilyDependentId_SeriesCode_DoseIndex",
                schema: "medical",
                table: "Vaccinations",
                columns: new[] { "FamilyDependentId", "SeriesCode", "DoseIndex" },
                unique: true,
                filter: "\"FamilyDependentId\" IS NOT NULL AND \"SeriesCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Vaccinations_SubjectUserId",
                schema: "medical",
                table: "Vaccinations",
                column: "SubjectUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Vaccinations_SubjectUserId_SeriesCode_DoseIndex",
                schema: "medical",
                table: "Vaccinations",
                columns: new[] { "SubjectUserId", "SeriesCode", "DoseIndex" },
                unique: true,
                filter: "\"SubjectUserId\" IS NOT NULL AND \"SeriesCode\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VaccinationCertificates",
                schema: "medical");

            migrationBuilder.DropTable(
                name: "Vaccinations",
                schema: "medical");

            migrationBuilder.DropColumn(
                name: "IncludeVaccinations",
                schema: "medical",
                table: "DoctorReports");
        }
    }
}
