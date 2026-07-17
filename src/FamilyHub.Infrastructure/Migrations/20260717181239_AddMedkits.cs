using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMedkits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MedkitId",
                table: "Medications",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Medkits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Medkits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Medkits_Families_FamilyId",
                        column: x => x.FamilyId,
                        principalTable: "Families",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Medications_MedkitId",
                table: "Medications",
                column: "MedkitId");

            migrationBuilder.CreateIndex(
                name: "IX_Medkits_FamilyId",
                table: "Medkits",
                column: "FamilyId");

            migrationBuilder.AddForeignKey(
                name: "FK_Medications_Medkits_MedkitId",
                table: "Medications",
                column: "MedkitId",
                principalTable: "Medkits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Medications_Medkits_MedkitId",
                table: "Medications");

            migrationBuilder.DropTable(
                name: "Medkits");

            migrationBuilder.DropIndex(
                name: "IX_Medications_MedkitId",
                table: "Medications");

            migrationBuilder.DropColumn(
                name: "MedkitId",
                table: "Medications");
        }
    }
}
