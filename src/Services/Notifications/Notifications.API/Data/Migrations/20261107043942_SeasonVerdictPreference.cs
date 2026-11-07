using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeasonVerdictPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SeasonVerdicts",
                table: "preferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Verdicts followed "new seasons" until now, so each user keeps what they had.
            migrationBuilder.Sql("""UPDATE preferences SET "SeasonVerdicts" = "NewSeasons";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeasonVerdicts",
                table: "preferences");
        }
    }
}
