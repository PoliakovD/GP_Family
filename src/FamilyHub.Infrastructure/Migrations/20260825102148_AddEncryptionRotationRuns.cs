using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <summary>
    /// Закрывает пробел, найденный по CI-падению интеграционных тестов после PR #1 (medicalrecords):
    /// EncryptionRotationRun (ADR-0009, admin-панель) имела DbSet и EF-конфигурацию, but ни одна
    /// миграция никогда не создавала саму таблицу — AddFileAttachmentKeyId (uncommitted-миграция,
    /// пришедшая вместе с admin-panel) добавляла только FileAttachments.KeyId, а не эту таблицу.
    /// Модель-снапшот при этом уже "знала" про EncryptionRotationRuns (унаследовано из более
    /// раннего, впоследствии перегенерированного черновика миграции medicalrecords), поэтому
    /// `dotnet ef migrations add` перестал видеть разницу и генерировал пустую миграцию — этот файл
    /// написан руками по факту существующей EncryptionRotationRunConfiguration, не автогенерацией.
    /// </summary>
    public partial class AddEncryptionRotationRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EncryptionRotationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKeyId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FieldsStepIndex = table.Column<int>(type: "integer", nullable: false),
                    FieldsCursorId = table.Column<Guid>(type: "uuid", nullable: true),
                    FieldsProcessed = table.Column<int>(type: "integer", nullable: false),
                    FieldsTotal = table.Column<int>(type: "integer", nullable: false),
                    BlobsCursorId = table.Column<Guid>(type: "uuid", nullable: true),
                    BlobsProcessed = table.Column<int>(type: "integer", nullable: false),
                    BlobsTotal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncryptionRotationRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EncryptionRotationRuns_StartedAt",
                table: "EncryptionRotationRuns",
                column: "StartedAt");

            // Не более одного активного прогона одновременно (Status=Running=0) — см.
            // EncryptionRotationRunConfiguration/AdminKeysService.
            migrationBuilder.CreateIndex(
                name: "IX_EncryptionRotationRuns_Status",
                table: "EncryptionRotationRuns",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EncryptionRotationRuns");
        }
    }
}
