using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MoveTablesToDomainSchemas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.EnsureSchema(
                name: "medical");

            migrationBuilder.RenameTable(
                name: "Users",
                newName: "Users",
                newSchema: "identity");

            migrationBuilder.RenameTable(
                name: "Notifications",
                newName: "Notifications",
                newSchema: "identity");

            migrationBuilder.RenameTable(
                name: "Medkits",
                newName: "Medkits",
                newSchema: "medical");

            migrationBuilder.RenameTable(
                name: "Medications",
                newName: "Medications",
                newSchema: "medical");

            migrationBuilder.RenameTable(
                name: "MedicalRecords",
                newName: "MedicalRecords",
                newSchema: "medical");

            migrationBuilder.RenameTable(
                name: "MedicalRecordHiddens",
                newName: "MedicalRecordHiddens",
                newSchema: "medical");

            migrationBuilder.RenameTable(
                name: "FileAttachments",
                newName: "FileAttachments",
                newSchema: "medical");

            migrationBuilder.RenameTable(
                name: "FamilyMembers",
                newName: "FamilyMembers",
                newSchema: "identity");

            migrationBuilder.RenameTable(
                name: "FamilyMedicalShares",
                newName: "FamilyMedicalShares",
                newSchema: "medical");

            migrationBuilder.RenameTable(
                name: "FamilyInvites",
                newName: "FamilyInvites",
                newSchema: "identity");

            migrationBuilder.RenameTable(
                name: "FamilyInviteRedemptions",
                newName: "FamilyInviteRedemptions",
                newSchema: "identity");

            migrationBuilder.RenameTable(
                name: "Families",
                newName: "Families",
                newSchema: "identity");

            migrationBuilder.RenameTable(
                name: "Birthdays",
                newName: "Birthdays",
                newSchema: "identity");

            migrationBuilder.AlterColumn<string>(
                name: "PersonName",
                schema: "medical",
                table: "MedicalRecords",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "FileName",
                schema: "medical",
                table: "FileAttachments",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300);

            migrationBuilder.AlterColumn<string>(
                name: "PersonName",
                schema: "identity",
                table: "Birthdays",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "Users",
                schema: "identity",
                newName: "Users");

            migrationBuilder.RenameTable(
                name: "Notifications",
                schema: "identity",
                newName: "Notifications");

            migrationBuilder.RenameTable(
                name: "Medkits",
                schema: "medical",
                newName: "Medkits");

            migrationBuilder.RenameTable(
                name: "Medications",
                schema: "medical",
                newName: "Medications");

            migrationBuilder.RenameTable(
                name: "MedicalRecords",
                schema: "medical",
                newName: "MedicalRecords");

            migrationBuilder.RenameTable(
                name: "MedicalRecordHiddens",
                schema: "medical",
                newName: "MedicalRecordHiddens");

            migrationBuilder.RenameTable(
                name: "FileAttachments",
                schema: "medical",
                newName: "FileAttachments");

            migrationBuilder.RenameTable(
                name: "FamilyMembers",
                schema: "identity",
                newName: "FamilyMembers");

            migrationBuilder.RenameTable(
                name: "FamilyMedicalShares",
                schema: "medical",
                newName: "FamilyMedicalShares");

            migrationBuilder.RenameTable(
                name: "FamilyInvites",
                schema: "identity",
                newName: "FamilyInvites");

            migrationBuilder.RenameTable(
                name: "FamilyInviteRedemptions",
                schema: "identity",
                newName: "FamilyInviteRedemptions");

            migrationBuilder.RenameTable(
                name: "Families",
                schema: "identity",
                newName: "Families");

            migrationBuilder.RenameTable(
                name: "Birthdays",
                schema: "identity",
                newName: "Birthdays");

            migrationBuilder.AlterColumn<string>(
                name: "PersonName",
                table: "MedicalRecords",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "FileName",
                table: "FileAttachments",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "PersonName",
                table: "Birthdays",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
