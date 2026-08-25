using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReworkPersonIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayName",
                schema: "identity",
                table: "Users");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "identity",
                table: "FamilyDependents",
                newName: "FirstName");

            migrationBuilder.AddColumn<DateOnly>(
                name: "BirthDate",
                schema: "identity",
                table: "Users",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                schema: "identity",
                table: "Users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Gender",
                schema: "identity",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                schema: "identity",
                table: "Users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MiddleName",
                schema: "identity",
                table: "Users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Gender",
                schema: "identity",
                table: "FamilyDependents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                schema: "identity",
                table: "FamilyDependents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MiddleName",
                schema: "identity",
                table: "FamilyDependents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BirthDate",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FirstName",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Gender",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastName",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MiddleName",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Gender",
                schema: "identity",
                table: "FamilyDependents");

            migrationBuilder.DropColumn(
                name: "LastName",
                schema: "identity",
                table: "FamilyDependents");

            migrationBuilder.DropColumn(
                name: "MiddleName",
                schema: "identity",
                table: "FamilyDependents");

            migrationBuilder.RenameColumn(
                name: "FirstName",
                schema: "identity",
                table: "FamilyDependents",
                newName: "Name");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                schema: "identity",
                table: "Users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }
    }
}
