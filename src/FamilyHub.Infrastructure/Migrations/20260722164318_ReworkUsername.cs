using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReworkUsername : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TgUsername",
                schema: "identity",
                table: "Users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Перенос данных (НЕ просто rename — колонка Username меняет смысл):
            // 1) то, что раньше называлось Username (зеркало Telegram @handle), переносится
            //    в TgUsername как есть — семантика этого атрибута не менялась;
            // 2) Username очищается и пересчитывается заново как отдельный уникальный видимый
            //    идентификатор: заполняется только для тех, чей бывший TG-хэндл после
            //    нормализации (lower) проходит новый формат (^[a-z][a-z0-9_]{4,31}$) и не
            //    коллизирует с хэндлом другого пользователя после нормализации.
            // Остальным Username остаётся NULL — это не блокирует вход (не идентификатор
            // аутентификации), назначить свой видимый username позже — отдельная задача.
            migrationBuilder.Sql(
                "UPDATE identity.\"Users\" SET \"TgUsername\" = \"Username\" WHERE \"Username\" IS NOT NULL;");

            migrationBuilder.Sql(
                "UPDATE identity.\"Users\" SET \"Username\" = NULL WHERE \"Username\" IS NOT NULL;");

            migrationBuilder.Sql("""
                UPDATE identity."Users" u
                SET "Username" = lower(u."TgUsername")
                WHERE u."TgUsername" IS NOT NULL
                  AND lower(u."TgUsername") ~ '^[a-z][a-z0-9_]{4,31}$'
                  AND NOT EXISTS (
                      SELECT 1 FROM identity."Users" o
                      WHERE o."Id" <> u."Id" AND lower(o."TgUsername") = lower(u."TgUsername")
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                schema: "identity",
                table: "Users",
                column: "Username",
                unique: true,
                filter: "\"Username\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                schema: "identity",
                table: "Users");

            // Восстановить прежнюю семантику Username = Telegram-хэндл (значение всё время
            // жило в TgUsername после Up, ничего не потеряно).
            migrationBuilder.Sql("UPDATE identity.\"Users\" SET \"Username\" = \"TgUsername\";");

            migrationBuilder.DropColumn(
                name: "TgUsername",
                schema: "identity",
                table: "Users");
        }
    }
}
