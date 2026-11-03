using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ratings.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class EpisodeDuels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RecordedAtUtc",
                table: "watched_episodes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "ShowId",
                table: "watched_episodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "episode_duels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    WinnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episode_duels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_watched_episodes_UserId_ShowId",
                table: "watched_episodes",
                columns: new[] { "UserId", "ShowId" });

            migrationBuilder.CreateIndex(
                name: "IX_episode_duels_UserId_ShowId",
                table: "episode_duels",
                columns: new[] { "UserId", "ShowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "episode_duels");

            migrationBuilder.DropIndex(
                name: "IX_watched_episodes_UserId_ShowId",
                table: "watched_episodes");

            migrationBuilder.DropColumn(
                name: "RecordedAtUtc",
                table: "watched_episodes");

            migrationBuilder.DropColumn(
                name: "ShowId",
                table: "watched_episodes");
        }
    }
}
