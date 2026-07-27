using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenamePinToPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // RenameColumn, а не Drop+Add: эти колонки хранят живые учётные данные (PBKDF2-хеш
            // пароля и счётчик неудачных попыток входа) уже существующих пользователей —
            // drop+add потеряло бы их и разлогинило бы всех навсегда. Первый RenameColumn в
            // этом репозитории (грепом подтверждено — прецедентов не было), поэтому явный
            // комментарий: это не случайность, EF сам корректно определил переименование.
            migrationBuilder.RenameColumn(
                name: "PinHash",
                schema: "identity",
                table: "Users",
                newName: "PasswordHash");

            migrationBuilder.RenameColumn(
                name: "FailedPinAttempts",
                schema: "identity",
                table: "Users",
                newName: "FailedLoginAttempts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PasswordHash",
                schema: "identity",
                table: "Users",
                newName: "PinHash");

            migrationBuilder.RenameColumn(
                name: "FailedLoginAttempts",
                schema: "identity",
                table: "Users",
                newName: "FailedPinAttempts");
        }
    }
}
