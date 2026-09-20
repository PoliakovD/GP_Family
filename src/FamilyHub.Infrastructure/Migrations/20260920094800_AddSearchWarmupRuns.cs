using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchWarmupRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SearchWarmupRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<int>(type: "integer", nullable: false),
                    SpecimenKbId = table.Column<Guid>(type: "uuid", nullable: true),
                    NamesJson = table.Column<string>(type: "text", nullable: false),
                    Cursor = table.Column<int>(type: "integer", nullable: false),
                    TotalNames = table.Column<int>(type: "integer", nullable: false),
                    PaidCalls = table.Column<int>(type: "integer", nullable: false),
                    SkippedKbHit = table.Column<int>(type: "integer", nullable: false),
                    SkippedFreshCache = table.Column<int>(type: "integer", nullable: false),
                    Failures = table.Column<int>(type: "integer", nullable: false),
                    MaxPaidCalls = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchWarmupRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchWarmupRuns_StartedAt",
                table: "SearchWarmupRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SearchWarmupRuns_Status",
                table: "SearchWarmupRuns",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchWarmupRuns");
        }
    }
}
