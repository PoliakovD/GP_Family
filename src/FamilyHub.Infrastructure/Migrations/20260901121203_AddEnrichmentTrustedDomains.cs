using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentTrustedDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OverridesJson",
                schema: "kb",
                table: "medication_search_cache",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OverridesJson",
                schema: "kb",
                table: "lab_analyte_search_cache",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EnrichmentTrustedDomains",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<int>(type: "integer", nullable: false),
                    Domain = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrichmentTrustedDomains", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentTrustedDomains_Topic_Domain",
                schema: "medical",
                table: "EnrichmentTrustedDomains",
                columns: new[] { "Topic", "Domain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentTrustedDomains_Topic_Rank",
                schema: "medical",
                table: "EnrichmentTrustedDomains",
                columns: new[] { "Topic", "Rank" });

            // Сид тех же значений, что раньше были статикой в EnrichmentOptions.TrustedDomains/
            // AnalyteTrustedDomains — поведение системы не должно измениться сразу после этой
            // миграции, только стать редактируемым через админку. Topic: 0=Medication, 1=LabAnalyte
            // (см. FamilyHub.Domain.Enums.WebSearchTopic). Порядок = Rank, значим для LabAnalyte
            // (ReferenceRangeMerger).
            var now = DateTime.UtcNow;
            migrationBuilder.InsertData(
                schema: "medical",
                table: "EnrichmentTrustedDomains",
                columns: new[] { "Id", "Topic", "Domain", "Rank", "IsEnabled", "CreatedAt" },
                values: new object[,]
                {
                    { Guid.NewGuid(), 0, "grls.rosminzdrav.ru", 0, true, now },
                    { Guid.NewGuid(), 0, "vidal.ru", 1, true, now },
                    { Guid.NewGuid(), 0, "rlsnet.ru", 2, true, now },
                    { Guid.NewGuid(), 1, "invitro.ru", 0, true, now },
                    { Guid.NewGuid(), 1, "gemotest.ru", 1, true, now },
                    { Guid.NewGuid(), 1, "helix.ru", 2, true, now },
                    { Guid.NewGuid(), 1, "kdlmed.ru", 3, true, now },
                    { Guid.NewGuid(), 1, "cmd-online.ru", 4, true, now },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnrichmentTrustedDomains",
                schema: "medical");

            migrationBuilder.DropColumn(
                name: "OverridesJson",
                schema: "kb",
                table: "medication_search_cache");

            migrationBuilder.DropColumn(
                name: "OverridesJson",
                schema: "kb",
                table: "lab_analyte_search_cache");
        }
    }
}
