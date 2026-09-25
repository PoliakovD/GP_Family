using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitingForAiAndPendingSpecimen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingSpecimenText",
                schema: "medical",
                table: "MedicalRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WaitingForAi",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingSpecimenText",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropColumn(
                name: "WaitingForAi",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs");
        }
    }
}
