using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Research.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class EpisodeNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "episode_notes",
                columns: table => new
                {
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "integer", nullable: false),
                    Mark = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    NotedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotedById = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NotedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_episode_notes", x => x.EpisodeId);
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

            migrationBuilder.CreateIndex(
                name: "IX_episode_notes_ShowId",
                table: "episode_notes",
                column: "ShowId");

            migrationBuilder.CreateIndex(
                name: "IX_integration_event_log_State_CreatedAtUtc",
                table: "integration_event_log",
                columns: new[] { "State", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "episode_notes");

            migrationBuilder.DropTable(
                name: "integration_event_log");

            migrationBuilder.DropTable(
                name: "processed_integration_events");
        }
    }
}
