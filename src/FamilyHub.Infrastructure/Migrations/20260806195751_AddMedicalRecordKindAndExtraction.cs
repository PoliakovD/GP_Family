using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMedicalRecordKindAndExtraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtractedDataJson",
                schema: "medical",
                table: "MedicalRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExtractionStatus",
                schema: "medical",
                table: "MedicalRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                schema: "medical",
                table: "MedicalRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_MedicalRecords_OwnerUserId_Kind",
                schema: "medical",
                table: "MedicalRecords",
                columns: new[] { "OwnerUserId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MedicalRecords_OwnerUserId_Kind",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropColumn(
                name: "ExtractedDataJson",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropColumn(
                name: "ExtractionStatus",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "medical",
                table: "MedicalRecords");
        }
    }
}
