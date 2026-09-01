using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalSpecimenKb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "global_specimens_kb",
                schema: "kb",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_specimens_kb", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_global_specimens_kb_NormalizedName",
                schema: "kb",
                table: "global_specimens_kb",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "global_specimens_kb",
                schema: "kb");
        }
    }
}
