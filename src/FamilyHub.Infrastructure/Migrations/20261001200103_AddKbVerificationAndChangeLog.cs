using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKbVerificationAndChangeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VerificationStatus",
                schema: "kb",
                table: "global_medications_kb",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAt",
                schema: "kb",
                table: "global_medications_kb",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedPayloadHash",
                schema: "kb",
                table: "global_medications_kb",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerificationStatus",
                schema: "kb",
                table: "global_lab_analytes_kb",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAt",
                schema: "kb",
                table: "global_lab_analytes_kb",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedPayloadHash",
                schema: "kb",
                table: "global_lab_analytes_kb",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "change_log",
                schema: "kb",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Target = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetLabel = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BeforeJson = table.Column<string>(type: "text", nullable: true),
                    AfterJson = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    RevertedLogId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_change_log_At",
                schema: "kb",
                table: "change_log",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_change_log_Target_TargetId_At",
                schema: "kb",
                table: "change_log",
                columns: new[] { "Target", "TargetId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_log",
                schema: "kb");

            migrationBuilder.DropColumn(
                name: "VerificationStatus",
                schema: "kb",
                table: "global_medications_kb");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                schema: "kb",
                table: "global_medications_kb");

            migrationBuilder.DropColumn(
                name: "VerifiedPayloadHash",
                schema: "kb",
                table: "global_medications_kb");

            migrationBuilder.DropColumn(
                name: "VerificationStatus",
                schema: "kb",
                table: "global_lab_analytes_kb");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                schema: "kb",
                table: "global_lab_analytes_kb");

            migrationBuilder.DropColumn(
                name: "VerifiedPayloadHash",
                schema: "kb",
                table: "global_lab_analytes_kb");
        }
    }
}
