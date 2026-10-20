using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Analytics.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "announced_seasons",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    PremieresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announced_seasons", x => new { x.ShowId, x.SeasonNumber });
                });

            migrationBuilder.CreateTable(
                name: "cast_credits",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Character = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CharacterExternalIds = table.Column<List<string>>(type: "text[]", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cast_credits", x => new { x.ShowId, x.PersonName });
                });

            migrationBuilder.CreateTable(
                name: "episodes",
                columns: table => new
                {
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AirsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RuntimeMinutes = table.Column<int>(type: "integer", nullable: true),
                    ExternalRating = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episodes", x => x.EpisodeId);
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
                name: "premieres",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    ShowTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PosterUrl = table.Column<string>(type: "text", nullable: true),
                    Genres = table.Column<List<string>>(type: "text[]", nullable: false),
                    PremieresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ShowExternalRating = table.Column<double>(type: "double precision", nullable: true),
                    DaysSincePreviousSeason = table.Column<int>(type: "integer", nullable: true),
                    CatchUpEpisodes = table.Column<int>(type: "integer", nullable: false),
                    CatchUpRuntimeMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_premieres", x => new { x.ShowId, x.SeasonNumber });
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
                name: "show_insights",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_show_insights", x => x.ShowId);
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
                    Popularity = table.Column<double>(type: "double precision", nullable: false),
                    ExternalRating = table.Column<double>(type: "double precision", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shows", x => x.ShowId);
                });

            migrationBuilder.CreateTable(
                name: "top_episodes",
                columns: table => new
                {
                    Genre = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PosterUrl = table.Column<string>(type: "text", nullable: true),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AirsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExternalRating = table.Column<double>(type: "double precision", nullable: false),
                    OurRating = table.Column<double>(type: "double precision", nullable: true),
                    OurRatingCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_top_episodes", x => new { x.Genre, x.Rank });
                });

            migrationBuilder.CreateTable(
                name: "user_episode_ratings",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stars = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_episode_ratings", x => new { x.UserId, x.EpisodeId });
                });

            migrationBuilder.CreateTable(
                name: "user_show_ratings",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stars = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_show_ratings", x => new { x.UserId, x.ShowId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_cast_credits_PersonName",
                table: "cast_credits",
                column: "PersonName");

            migrationBuilder.CreateIndex(
                name: "IX_episodes_ShowId",
                table: "episodes",
                column: "ShowId");

            migrationBuilder.CreateIndex(
                name: "IX_integration_event_log_State_CreatedAtUtc",
                table: "integration_event_log",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_premieres_PremieresAtUtc",
                table: "premieres",
                column: "PremieresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_user_episode_ratings_ShowId",
                table: "user_episode_ratings",
                column: "ShowId");

            migrationBuilder.CreateIndex(
                name: "IX_user_show_ratings_ShowId",
                table: "user_show_ratings",
                column: "ShowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "announced_seasons");

            migrationBuilder.DropTable(
                name: "cast_credits");

            migrationBuilder.DropTable(
                name: "episodes");

            migrationBuilder.DropTable(
                name: "integration_event_log");

            migrationBuilder.DropTable(
                name: "premieres");

            migrationBuilder.DropTable(
                name: "processed_integration_events");

            migrationBuilder.DropTable(
                name: "show_insights");

            migrationBuilder.DropTable(
                name: "shows");

            migrationBuilder.DropTable(
                name: "top_episodes");

            migrationBuilder.DropTable(
                name: "user_episode_ratings");

            migrationBuilder.DropTable(
                name: "user_show_ratings");
        }
    }
}
