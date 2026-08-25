using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFileAttachmentKeyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KeyId",
                schema: "medical",
                table: "FileAttachments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            // Бэкфилл (ADR-0009): до этой миграции ротации ключей не существовало, поэтому
            // ЛЮБОЙ уже зашифрованный блоб зашифрован единственным ключом, который вообще мог
            // быть активным — дефолтным "v1" (EncryptionOptions.ActiveKeyId). Незашифрованные
            // legacy-вложения (IsEncrypted=false) оставляем с KeyId=NULL — колонка описывает
            // ключ шифрования, а не применима к ним.
            migrationBuilder.Sql(
                """UPDATE medical."FileAttachments" SET "KeyId" = 'v1' WHERE "IsEncrypted" = true""");

            migrationBuilder.CreateIndex(
                name: "IX_FileAttachments_IsEncrypted_KeyId",
                schema: "medical",
                table: "FileAttachments",
                columns: new[] { "IsEncrypted", "KeyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FileAttachments_IsEncrypted_KeyId",
                schema: "medical",
                table: "FileAttachments");

            migrationBuilder.DropColumn(
                name: "KeyId",
                schema: "medical",
                table: "FileAttachments");
        }
    }
}
