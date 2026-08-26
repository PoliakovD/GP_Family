using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReworkAnalysisEnrichmentV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MedicalDocumentExtractionJobs_AttachmentId",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs");

            migrationBuilder.DropIndex(
                name: "IX_MedicalDocumentExtractionJobs_MedicalRecordId",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs");

            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_MedicalRecordId",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropColumn(
                name: "PersonName",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropColumn(
                name: "AttachmentId",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs");

            migrationBuilder.AddColumn<string>(
                name: "Title",
                schema: "medical",
                table: "MedicalRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProcessedFiles",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalFiles",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RefSource",
                schema: "medical",
                table: "LabIndicators",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Specimen",
                schema: "medical",
                table: "LabIndicators",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExtractedAt",
                schema: "medical",
                table: "FileAttachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MedicalDocumentExtractionJobs_MedicalRecordId",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                column: "MedicalRecordId",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MedicalDocumentExtractionJobs_MedicalRecordId",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs");

            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_MedicalRecordId_AnalyteKey_Specimen",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey_Specimen",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropColumn(
                name: "Title",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropColumn(
                name: "ProcessedFiles",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs");

            migrationBuilder.DropColumn(
                name: "TotalFiles",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs");

            migrationBuilder.DropColumn(
                name: "RefSource",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropColumn(
                name: "Specimen",
                schema: "medical",
                table: "LabIndicators");

            migrationBuilder.DropColumn(
                name: "ExtractedAt",
                schema: "medical",
                table: "FileAttachments");

            migrationBuilder.AddColumn<string>(
                name: "PersonName",
                schema: "medical",
                table: "MedicalRecords",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "AttachmentId",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_MedicalDocumentExtractionJobs_AttachmentId",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                column: "AttachmentId",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_MedicalDocumentExtractionJobs_MedicalRecordId",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                column: "MedicalRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_MedicalRecordId",
                schema: "medical",
                table: "LabIndicators",
                column: "MedicalRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_OwnerUserId_AnalyteKey",
                schema: "medical",
                table: "LabIndicators",
                columns: new[] { "OwnerUserId", "AnalyteKey" });
        }
    }
}
