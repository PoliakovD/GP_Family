using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDoctorReportSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SubjectFamilyDependentId",
                schema: "medical",
                table: "DoctorReports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubjectUserId",
                schema: "medical",
                table: "DoctorReports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DoctorReports_SubjectFamilyDependentId",
                schema: "medical",
                table: "DoctorReports",
                column: "SubjectFamilyDependentId");

            migrationBuilder.CreateIndex(
                name: "IX_DoctorReports_SubjectUserId",
                schema: "medical",
                table: "DoctorReports",
                column: "SubjectUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DoctorReports_SubjectFamilyDependentId",
                schema: "medical",
                table: "DoctorReports");

            migrationBuilder.DropIndex(
                name: "IX_DoctorReports_SubjectUserId",
                schema: "medical",
                table: "DoctorReports");

            migrationBuilder.DropColumn(
                name: "SubjectFamilyDependentId",
                schema: "medical",
                table: "DoctorReports");

            migrationBuilder.DropColumn(
                name: "SubjectUserId",
                schema: "medical",
                table: "DoctorReports");
        }
    }
}
