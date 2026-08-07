using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFamilyDependentsAndRecordTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FamilyDependentId",
                schema: "medical",
                table: "MedicalRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetUserId",
                schema: "medical",
                table: "MedicalRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FamilyDependents",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsPet = table.Column<bool>(type: "boolean", nullable: false),
                    PetSpecies = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamilyDependents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamilyDependents_Families_FamilyId",
                        column: x => x.FamilyId,
                        principalSchema: "identity",
                        principalTable: "Families",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MedicalRecords_FamilyDependentId",
                schema: "medical",
                table: "MedicalRecords",
                column: "FamilyDependentId");

            migrationBuilder.CreateIndex(
                name: "IX_MedicalRecords_TargetUserId",
                schema: "medical",
                table: "MedicalRecords",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FamilyDependents_FamilyId",
                schema: "identity",
                table: "FamilyDependents",
                column: "FamilyId");

            migrationBuilder.AddForeignKey(
                name: "FK_MedicalRecords_FamilyDependents_FamilyDependentId",
                schema: "medical",
                table: "MedicalRecords",
                column: "FamilyDependentId",
                principalSchema: "identity",
                principalTable: "FamilyDependents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MedicalRecords_FamilyDependents_FamilyDependentId",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropTable(
                name: "FamilyDependents",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "IX_MedicalRecords_FamilyDependentId",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropIndex(
                name: "IX_MedicalRecords_TargetUserId",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropColumn(
                name: "FamilyDependentId",
                schema: "medical",
                table: "MedicalRecords");

            migrationBuilder.DropColumn(
                name: "TargetUserId",
                schema: "medical",
                table: "MedicalRecords");
        }
    }
}
