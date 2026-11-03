using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShowCreatives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CreativesImported",
                table: "shows",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CareerCreated",
                table: "people",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CareerExecutiveProduced",
                table: "people",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "CareerNotableShows",
                table: "people",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<DateTime>(
                name: "CareerRefreshedAtUtc",
                table: "people",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CareerShows",
                table: "people",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "show_creatives",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Roles = table.Column<List<string>>(type: "text[]", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_show_creatives", x => new { x.ShowId, x.PersonId });
                    table.ForeignKey(
                        name: "FK_show_creatives_people_PersonId",
                        column: x => x.PersonId,
                        principalTable: "people",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_show_creatives_shows_ShowId",
                        column: x => x.ShowId,
                        principalTable: "shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_show_creatives_PersonId",
                table: "show_creatives",
                column: "PersonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "show_creatives");

            migrationBuilder.DropColumn(
                name: "CreativesImported",
                table: "shows");

            migrationBuilder.DropColumn(
                name: "CareerCreated",
                table: "people");

            migrationBuilder.DropColumn(
                name: "CareerExecutiveProduced",
                table: "people");

            migrationBuilder.DropColumn(
                name: "CareerNotableShows",
                table: "people");

            migrationBuilder.DropColumn(
                name: "CareerRefreshedAtUtc",
                table: "people");

            migrationBuilder.DropColumn(
                name: "CareerShows",
                table: "people");
        }
    }
}
