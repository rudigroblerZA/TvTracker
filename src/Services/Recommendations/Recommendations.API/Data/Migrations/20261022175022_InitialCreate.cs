using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recommendations.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dirty_users",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MarkedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dirty_users", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "dismissals",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    DismissedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dismissals", x => new { x.UserId, x.ShowId });
                });

            migrationBuilder.CreateTable(
                name: "integration_event_log",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventTypeName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    TimesSent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_event_log", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "processed_integration_events",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Handler = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_integration_events", x => new { x.EventId, x.Handler });
                });

            migrationBuilder.CreateTable(
                name: "shows",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PosterUrl = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Genres = table.Column<List<string>>(type: "text[]", nullable: false),
                    Keywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    CastNames = table.Column<List<string>>(type: "text[]", nullable: false),
                    SimilarShowIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Popularity = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shows", x => x.ShowId);
                });

            migrationBuilder.CreateTable(
                name: "user_recommendations",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_recommendations", x => new { x.UserId, x.ShowId });
                });

            migrationBuilder.CreateTable(
                name: "user_show_signals",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Followed = table.Column<bool>(type: "boolean", nullable: false),
                    EpisodesWatched = table.Column<int>(type: "integer", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_show_signals", x => new { x.UserId, x.ShowId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_integration_event_log_State_CreatedAtUtc",
                table: "integration_event_log",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_user_recommendations_UserId_Rank",
                table: "user_recommendations",
                columns: new[] { "UserId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_user_show_signals_ShowId_Followed",
                table: "user_show_signals",
                columns: new[] { "ShowId", "Followed" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dirty_users");

            migrationBuilder.DropTable(
                name: "dismissals");

            migrationBuilder.DropTable(
                name: "integration_event_log");

            migrationBuilder.DropTable(
                name: "processed_integration_events");

            migrationBuilder.DropTable(
                name: "shows");

            migrationBuilder.DropTable(
                name: "user_recommendations");

            migrationBuilder.DropTable(
                name: "user_show_signals");
        }
    }
}
