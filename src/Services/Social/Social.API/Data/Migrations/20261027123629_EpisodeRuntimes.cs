using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class EpisodeRuntimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RuntimeMinutes",
                table: "episodes",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RuntimeMinutes",
                table: "episodes");
        }
    }
}
