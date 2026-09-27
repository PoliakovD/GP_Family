using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthShareGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthShareGrants",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ViewerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Categories = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthShareGrants", x => x.Id);
                    table.CheckConstraint("CK_HealthShareGrants_NotSelf", "\"OwnerUserId\" <> \"ViewerUserId\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthShareGrants_OwnerUserId_ViewerUserId",
                schema: "medical",
                table: "HealthShareGrants",
                columns: new[] { "OwnerUserId", "ViewerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthShareGrants_ViewerUserId",
                schema: "medical",
                table: "HealthShareGrants",
                column: "ViewerUserId");

            // ADR-0017: существующие наблюдатели за взрослыми (MedicationWatcher.SubjectUserId != null)
            // до этой миграции давали доступ и к курсам, и (после ADR-0016) к прививкам сразу — переносим
            // их в гранты с обеими категориями (1 = Intake, 2 = Vaccinations, 3 = обе), чтобы никто не
            // потерял уже имевшийся доступ. Наблюдатели подопечных (FamilyDependentId) сюда не входят —
            // подопечные и так открыты любому активному члену семьи, гранты им не нужны.
            migrationBuilder.Sql("""
                INSERT INTO medical."HealthShareGrants" ("Id", "OwnerUserId", "ViewerUserId", "Categories", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), "SubjectUserId", "WatcherUserId", 3, now(), now()
                FROM medical."MedicationWatchers"
                WHERE "SubjectUserId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthShareGrants",
                schema: "medical");
        }
    }
}
