using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_people_Name",
                table: "people");

            migrationBuilder.AddColumn<DateOnly>(
                name: "Birthday",
                table: "people",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "people",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "Deathday",
                table: "people",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "people",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "people",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_people_ExternalId",
                table: "people",
                column: "ExternalId",
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_people_Name",
                table: "people",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_people_ExternalId",
                table: "people");

            migrationBuilder.DropIndex(
                name: "IX_people_Name",
                table: "people");

            migrationBuilder.DropColumn(
                name: "Birthday",
                table: "people");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "people");

            migrationBuilder.DropColumn(
                name: "Deathday",
                table: "people");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "people");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "people");

            migrationBuilder.CreateIndex(
                name: "IX_people_Name",
                table: "people",
                column: "Name",
                unique: true);
        }
    }
}
