using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestructureMedicationData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DataJson",
                table: "Medications",
                type: "jsonb",
                nullable: true);

            // Переносим старые Instructions/Quantity в DataJson ПЕРЕД тем, как дропнуть колонки —
            // иначе данные уже созданных медикаментов терялись бы безвозвратно.
            migrationBuilder.Sql("""
                UPDATE "Medications"
                SET "DataJson" = jsonb_strip_nulls(jsonb_build_object(
                    'instructions', "Instructions",
                    'quantity', "Quantity"::text
                ))
                """);

            migrationBuilder.DropColumn(
                name: "Instructions",
                table: "Medications");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "Medications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Instructions",
                table: "Medications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "Medications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE "Medications"
                SET "Instructions" = "DataJson" ->> 'instructions',
                    "Quantity" = COALESCE(("DataJson" ->> 'quantity')::int, 0)
                WHERE "DataJson" IS NOT NULL
                """);

            migrationBuilder.DropColumn(
                name: "DataJson",
                table: "Medications");
        }
    }
}
