using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchUploadAndSearchAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "KindIsAutoDetected",
                schema: "medical",
                table: "MedicalRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WebSearchCallLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Topic = table.Column<int>(type: "integer", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SpecimenDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    QueryText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    SnippetCount = table.Column<int>(type: "integer", nullable: false),
                    ResultUrlsJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    JobKind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebSearchCallLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebSearchCallLogs_NormalizedName",
                table: "WebSearchCallLogs",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_WebSearchCallLogs_OccurredAt",
                table: "WebSearchCallLogs",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_WebSearchCallLogs_Provider_OccurredAt",
                table: "WebSearchCallLogs",
                columns: new[] { "Provider", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebSearchCallLogs");

            migrationBuilder.DropColumn(
                name: "KindIsAutoDetected",
                schema: "medical",
                table: "MedicalRecords");
        }
    }
}
