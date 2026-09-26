using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMedicationCourses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "QuietHoursFrom",
                schema: "identity",
                table: "Users",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "QuietHoursTo",
                schema: "identity",
                table: "Users",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                schema: "identity",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MedicationCourses",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FamilyDependentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DrugName = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    PrescriptionText = table.Column<string>(type: "text", nullable: true),
                    ScheduleJson = table.Column<string>(type: "jsonb", nullable: false),
                    Food = table.Column<int>(type: "integer", nullable: false),
                    DoseUnit = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TimeZoneId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PausedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SourceMedicalRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourcePrescriptionIndex = table.Column<int>(type: "integer", nullable: true),
                    MedicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    WriteOffEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RepeatAfterMinutes = table.Column<int>(type: "integer", nullable: true),
                    MissedAfterMinutes = table.Column<int>(type: "integer", nullable: false),
                    LowStockDays = table.Column<int>(type: "integer", nullable: false),
                    LowStockNotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicationCourses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MedicationCourses_FamilyDependents_FamilyDependentId",
                        column: x => x.FamilyDependentId,
                        principalSchema: "identity",
                        principalTable: "FamilyDependents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MedicationCourses_Medications_MedicationId",
                        column: x => x.MedicationId,
                        principalSchema: "medical",
                        principalTable: "Medications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MedicationWatchers",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FamilyDependentId = table.Column<Guid>(type: "uuid", nullable: true),
                    WatcherUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiveReminders = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyMissed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicationWatchers", x => x.Id);
                    table.CheckConstraint("CK_MedicationWatchers_OneSubject", "(\"SubjectUserId\" IS NULL) <> (\"FamilyDependentId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_MedicationWatchers_FamilyDependents_FamilyDependentId",
                        column: x => x.FamilyDependentId,
                        principalSchema: "identity",
                        principalTable: "FamilyDependents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MedicationDoses",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Units = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TakenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SnoozedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SnoozeCount = table.Column<int>(type: "integer", nullable: false),
                    RemindedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RepeatSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HealthNoteId = table.Column<Guid>(type: "uuid", nullable: true),
                    WriteOffMedicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    WriteOffUnits = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicationDoses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MedicationDoses_MedicationCourses_CourseId",
                        column: x => x.CourseId,
                        principalSchema: "medical",
                        principalTable: "MedicationCourses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DoseActionTokens",
                schema: "medical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DoseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UsedAction = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoseActionTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DoseActionTokens_MedicationDoses_DoseId",
                        column: x => x.DoseId,
                        principalSchema: "medical",
                        principalTable: "MedicationDoses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DoseActionTokens_DoseId",
                schema: "medical",
                table: "DoseActionTokens",
                column: "DoseId");

            migrationBuilder.CreateIndex(
                name: "IX_DoseActionTokens_ExpiresAt",
                schema: "medical",
                table: "DoseActionTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_DoseActionTokens_RecipientUserId",
                schema: "medical",
                table: "DoseActionTokens",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DoseActionTokens_TokenHash",
                schema: "medical",
                table: "DoseActionTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MedicationCourses_FamilyDependentId",
                schema: "medical",
                table: "MedicationCourses",
                column: "FamilyDependentId");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationCourses_MedicationId",
                schema: "medical",
                table: "MedicationCourses",
                column: "MedicationId");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationCourses_Status",
                schema: "medical",
                table: "MedicationCourses",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationCourses_SubjectUserId_Status",
                schema: "medical",
                table: "MedicationCourses",
                columns: new[] { "SubjectUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MedicationDoses_CourseId_ScheduledAt",
                schema: "medical",
                table: "MedicationDoses",
                columns: new[] { "CourseId", "ScheduledAt" },
                unique: true,
                filter: "\"ScheduledAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationDoses_Status_ScheduledAt",
                schema: "medical",
                table: "MedicationDoses",
                columns: new[] { "Status", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MedicationWatchers_FamilyDependentId_WatcherUserId",
                schema: "medical",
                table: "MedicationWatchers",
                columns: new[] { "FamilyDependentId", "WatcherUserId" },
                unique: true,
                filter: "\"FamilyDependentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationWatchers_SubjectUserId_WatcherUserId",
                schema: "medical",
                table: "MedicationWatchers",
                columns: new[] { "SubjectUserId", "WatcherUserId" },
                unique: true,
                filter: "\"SubjectUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationWatchers_WatcherUserId",
                schema: "medical",
                table: "MedicationWatchers",
                column: "WatcherUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DoseActionTokens",
                schema: "medical");

            migrationBuilder.DropTable(
                name: "MedicationWatchers",
                schema: "medical");

            migrationBuilder.DropTable(
                name: "MedicationDoses",
                schema: "medical");

            migrationBuilder.DropTable(
                name: "MedicationCourses",
                schema: "medical");

            migrationBuilder.DropColumn(
                name: "QuietHoursFrom",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "QuietHoursTo",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                schema: "identity",
                table: "Users");
        }
    }
}
