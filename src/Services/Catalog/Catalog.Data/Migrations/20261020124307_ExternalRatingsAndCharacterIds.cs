using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExternalRatingsAndCharacterIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ExternalRating",
                table: "shows",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ExternalRating",
                table: "episodes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "CharacterExternalIds",
                table: "cast_members",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalRating",
                table: "shows");

            migrationBuilder.DropColumn(
                name: "ExternalRating",
                table: "episodes");

            migrationBuilder.DropColumn(
                name: "CharacterExternalIds",
                table: "cast_members");
        }
    }
}
