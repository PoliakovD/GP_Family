using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDoctorReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DoctorReports",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IncludeLabs = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeAiSummaries = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeMedications = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeVisits = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeMeasurements = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeSymptomsNotes = table.Column<bool>(type: "boolean", nullable: false),
                    PageCount = table.Column<int>(type: "integer", nullable: false),
                    Recipient = table.Column<string>(type: "text", nullable: true),
                    PatientComment = table.Column<string>(type: "text", nullable: true),
                    PatientSnapshotJson = table.Column<string>(type: "text", nullable: false),
                    ShareTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ShareToken = table.Column<string>(type: "text", nullable: true),
                    ShareExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ShareRevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ShareViewCount = table.Column<int>(type: "integer", nullable: false),
                    ShareLastViewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoctorReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorReports_OwnerUserId_CreatedAt",
                schema: "medical",
                table: "DoctorReports",
                columns: new[] { "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DoctorReports_ShareTokenHash",
                schema: "medical",
                table: "DoctorReports",
                column: "ShareTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DoctorReports",
                schema: "medical");
        }
    }
}
