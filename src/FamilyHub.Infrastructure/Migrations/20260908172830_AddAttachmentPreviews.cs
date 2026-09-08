using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachmentPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviewFailureReason",
                schema: "medical",
                table: "FileAttachments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PreviewGeneratedAt",
                schema: "medical",
                table: "FileAttachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviewStatus",
                schema: "medical",
                table: "FileAttachments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AttachmentPreviews",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    PageCount = table.Column<int>(type: "integer", nullable: true),
                    IsEncrypted = table.Column<bool>(type: "boolean", nullable: false),
                    KeyId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentPreviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttachmentPreviews_FileAttachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalSchema: "medical",
                        principalTable: "FileAttachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentPreviews_AttachmentId_Kind",
                schema: "medical",
                table: "AttachmentPreviews",
                columns: new[] { "AttachmentId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttachmentPreviews",
                schema: "medical");

            migrationBuilder.DropColumn(
                name: "PreviewFailureReason",
                schema: "medical",
                table: "FileAttachments");

            migrationBuilder.DropColumn(
                name: "PreviewGeneratedAt",
                schema: "medical",
                table: "FileAttachments");

            migrationBuilder.DropColumn(
                name: "PreviewStatus",
                schema: "medical",
                table: "FileAttachments");
        }
    }
}
