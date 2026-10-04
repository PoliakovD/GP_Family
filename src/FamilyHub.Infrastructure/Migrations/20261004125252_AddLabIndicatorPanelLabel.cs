using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Раздел бланка (LabIndicator.PanelLabel) — только отображение: не входит ни в уникальный индекс
    // показателя, ни в ключи справочника/кэша поиска, поэтому миграция — одна nullable-колонка без
    // бэкфилла (записи, распознанные раньше, остаются без разделов — плоская таблица, как было).
    public partial class AddLabIndicatorPanelLabel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PanelLabel",
                schema: "medical",
                table: "LabIndicators",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PanelLabel",
                schema: "medical",
                table: "LabIndicators");
        }
    }
}
