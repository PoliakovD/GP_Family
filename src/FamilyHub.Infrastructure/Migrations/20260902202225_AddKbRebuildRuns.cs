using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKbRebuildRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KbRebuildRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    StageIndex = table.Column<int>(type: "integer", nullable: false),
                    CacheMerged = table.Column<int>(type: "integer", nullable: false),
                    IndicatorsUpdated = table.Column<int>(type: "integer", nullable: false),
                    IndicatorsMerged = table.Column<int>(type: "integer", nullable: false),
                    CatalogDeleted = table.Column<int>(type: "integer", nullable: false),
                    ReseedRequested = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbRebuildRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KbRebuildRuns_StartedAt",
                table: "KbRebuildRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_KbRebuildRuns_Status",
                table: "KbRebuildRuns",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KbRebuildRuns");
        }
    }
}
