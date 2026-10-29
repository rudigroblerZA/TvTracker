using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    EpisodeIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Stars = table.Column<int>(type: "integer", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "episodes",
                columns: table => new
                {
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episodes", x => x.EpisodeId);
                });

            migrationBuilder.CreateTable(
                name: "follows",
                columns: table => new
                {
                    FollowerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FolloweeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_follows", x => new { x.FollowerId, x.FolloweeId });
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
                name: "profiles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ShareActivity = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profiles", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "shows",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PosterUrl = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shows", x => x.ShowId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_activities_UserId_OccurredAtUtc",
                table: "activities",
                columns: new[] { "UserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_episodes_ShowId",
                table: "episodes",
                column: "ShowId");

            migrationBuilder.CreateIndex(
                name: "IX_follows_FolloweeId",
                table: "follows",
                column: "FolloweeId");

            migrationBuilder.CreateIndex(
                name: "IX_integration_event_log_State_CreatedAtUtc",
                table: "integration_event_log",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_profiles_DisplayName",
                table: "profiles",
                column: "DisplayName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activities");

            migrationBuilder.DropTable(
                name: "episodes");

            migrationBuilder.DropTable(
                name: "follows");

            migrationBuilder.DropTable(
                name: "integration_event_log");

            migrationBuilder.DropTable(
                name: "processed_integration_events");

            migrationBuilder.DropTable(
                name: "profiles");

            migrationBuilder.DropTable(
                name: "shows");
        }
    }
}
