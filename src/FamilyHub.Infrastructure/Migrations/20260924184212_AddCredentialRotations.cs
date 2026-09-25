using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialRotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CredentialRotations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    FromIdentity = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ToIdentity = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GeneratedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TerminatedSessions = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredentialRotations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CredentialRotations_GeneratedAt",
                table: "CredentialRotations",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CredentialRotations_Kind",
                table: "CredentialRotations",
                column: "Kind",
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CredentialRotations");
        }
    }
}
