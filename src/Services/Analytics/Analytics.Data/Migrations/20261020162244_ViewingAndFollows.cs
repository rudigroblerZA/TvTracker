using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Analytics.Data.Migrations
{
    /// <inheritdoc />
    public partial class ViewingAndFollows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecentFollows",
                table: "premieres",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_episode_watches",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    WatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_episode_watches", x => new { x.UserId, x.EpisodeId });
                });

            migrationBuilder.CreateTable(
                name: "user_follows",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    FollowedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_follows", x => new { x.UserId, x.ShowId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_episode_watches_ShowId",
                table: "user_episode_watches",
                column: "ShowId");

            migrationBuilder.CreateIndex(
                name: "IX_user_follows_ShowId",
                table: "user_follows",
                column: "ShowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_episode_watches");

            migrationBuilder.DropTable(
                name: "user_follows");

            migrationBuilder.DropColumn(
                name: "RecentFollows",
                table: "premieres");
        }
    }
}
