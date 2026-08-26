using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UxRedesignVisitMedications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VisitMedicationEnrichmentJobs",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MedicalRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_VisitMedicationEnrichmentJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisitMedicationEnrichmentJobs_MedicalRecordId",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                column: "MedicalRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_VisitMedicationEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                column: "NormalizedName",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_VisitMedicationEnrichmentJobs_Status_CreatedAt",
                schema: "medical",
                table: "VisitMedicationEnrichmentJobs",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VisitMedicationEnrichmentJobs",
                schema: "medical");
        }
    }
}
