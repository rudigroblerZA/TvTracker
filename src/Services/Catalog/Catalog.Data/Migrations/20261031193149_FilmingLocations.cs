using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class FilmingLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_show_settings",
                table: "show_settings");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "show_settings",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "set");

            migrationBuilder.AddPrimaryKey(
                name: "PK_show_settings",
                table: "show_settings",
                columns: new[] { "ShowId", "PlaceId", "Kind" });

            // Every show is looked up again, now with its filming locations.
            migrationBuilder.Sql("DELETE FROM show_setting_lookups;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_show_settings",
                table: "show_settings");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "show_settings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_show_settings",
                table: "show_settings",
                columns: new[] { "ShowId", "PlaceId" });
        }
    }
}
