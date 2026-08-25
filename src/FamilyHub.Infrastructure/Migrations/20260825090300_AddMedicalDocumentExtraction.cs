using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <summary>
    /// Ветка medicalrecords (задачи 5.2/5.3): показатели анализов (LabIndicators), задачи
    /// конвейера извлечения (MedicalDocumentExtractionJobs), справочник показателей
    /// (kb.global_lab_analytes_kb) + его конвейер обогащения (LabAnalyteEnrichmentJobs),
    /// MedicalRecord.SummaryJson.
    /// </summary>
    public partial class AddMedicalDocumentExtraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SummaryJson",
                schema: "medical",
                table: "MedicalRecords",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "global_lab_analytes_kb",
                schema: "kb",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PayloadVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_lab_analytes_kb", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LabAnalyteEnrichmentJobs",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LabIndicatorId = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("PK_LabAnalyteEnrichmentJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LabIndicators",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MedicalRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalyteKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    KbAnalyteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Flag = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ValueRaw = table.Column<string>(type: "text", nullable: false),
                    ValueNumericText = table.Column<string>(type: "text", nullable: true),
                    Unit = table.Column<string>(type: "text", nullable: true),
                    RefLowText = table.Column<string>(type: "text", nullable: true),
                    RefHighText = table.Column<string>(type: "text", nullable: true),
                    RefText = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabIndicators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabIndicators_MedicalRecords_MedicalRecordId",
                        column: x => x.MedicalRecordId,
                        principalSchema: "medical",
                        principalTable: "MedicalRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MedicalDocumentExtractionJobs",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MedicalRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    IndicatorCount = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicalDocumentExtractionJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_global_lab_analytes_kb_NormalizedName",
                schema: "kb",
                table: "global_lab_analytes_kb",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabAnalyteEnrichmentJobs_LabIndicatorId",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                column: "LabIndicatorId");

            migrationBuilder.CreateIndex(
                name: "IX_LabAnalyteEnrichmentJobs_NormalizedName",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                column: "NormalizedName",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_LabAnalyteEnrichmentJobs_Status_CreatedAt",
                schema: "medical",
                table: "LabAnalyteEnrichmentJobs",
                columns: new[] { "Status", "CreatedAt" });

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

            migrationBuilder.CreateIndex(
                name: "IX_LabIndicators_OwnerUserId_Flag",
                schema: "medical",
                table: "LabIndicators",
                columns: new[] { "OwnerUserId", "Flag" });

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
                name: "IX_MedicalDocumentExtractionJobs_Status_CreatedAt",
                schema: "medical",
                table: "MedicalDocumentExtractionJobs",
                columns: new[] { "Status", "CreatedAt" });

            // --- kb.global_lab_analytes_kb: синонимы + полнотекстовый/триграммный поиск — тот же
            // паттерн, что global_medications_kb (AddMedicationEnrichment/AddFullTextSearch).
            migrationBuilder.Sql(
                "ALTER TABLE kb.global_lab_analytes_kb ADD COLUMN \"Aliases\" text[] NOT NULL DEFAULT '{}';");
            migrationBuilder.Sql(
                """CREATE INDEX "IX_global_lab_analytes_kb_Aliases" ON kb.global_lab_analytes_kb USING GIN ("Aliases");""");
            migrationBuilder.Sql("""
                ALTER TABLE kb.global_lab_analytes_kb
                    ADD COLUMN search_vector tsvector
                    GENERATED ALWAYS AS (
                        to_tsvector('russian',
                            coalesce("DisplayName", '') || ' ' ||
                            coalesce("NormalizedName", '') || ' ' ||
                            coalesce("PayloadJson"::text, ''))
                    ) STORED;
                """);
            migrationBuilder.Sql(
                """CREATE INDEX "IX_global_lab_analytes_kb_search_vector" ON kb.global_lab_analytes_kb USING GIN (search_vector);""");
            migrationBuilder.Sql(
                """CREATE INDEX "IX_global_lab_analytes_kb_DisplayName_trgm" ON kb.global_lab_analytes_kb USING GIN ("DisplayName" gin_trgm_ops);""");

            // --- medical.LabIndicators: триграммный поиск по AnalyteKey (SearchService.SearchIndicatorsAsync,
            // EF.Functions — не шифрован, plaintext, см. LabIndicatorConfiguration/докстринг LabIndicator).
            migrationBuilder.Sql(
                """CREATE INDEX "IX_LabIndicators_AnalyteKey_trgm" ON medical."LabIndicators" USING GIN ("AnalyteKey" gin_trgm_ops);""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "global_lab_analytes_kb",
                schema: "kb");

            migrationBuilder.DropTable(
                name: "LabAnalyteEnrichmentJobs",
                schema: "medical");

            migrationBuilder.DropTable(
                name: "LabIndicators",
                schema: "medical");

            migrationBuilder.DropTable(
                name: "MedicalDocumentExtractionJobs",
                schema: "medical");

            migrationBuilder.DropColumn(
                name: "SummaryJson",
                schema: "medical",
                table: "MedicalRecords");
        }
    }
}
