using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ratings.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommunityModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HiddenReason",
                table: "comments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "community_insights",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsSpoiler = table.Column<bool>(type: "boolean", nullable: false),
                    HiddenReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_community_insights", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "content_flags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Resolved = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_flags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "corrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Field = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SuggestedValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Context = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReviewedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corrections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "insight_votes",
                columns: table => new
                {
                    InsightId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_insight_votes", x => new { x.InsightId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "moderation_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModeratorName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_actions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_community_insights_ShowId_EpisodeId",
                table: "community_insights",
                columns: new[] { "ShowId", "EpisodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_community_insights_UserId_CreatedAtUtc",
                table: "community_insights",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_content_flags_Resolved",
                table: "content_flags",
                column: "Resolved");

            migrationBuilder.CreateIndex(
                name: "IX_content_flags_TargetKind_TargetId_UserId",
                table: "content_flags",
                columns: new[] { "TargetKind", "TargetId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_content_flags_UserId_CreatedAtUtc",
                table: "content_flags",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_corrections_Status_CreatedAtUtc",
                table: "corrections",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_corrections_UserId_CreatedAtUtc",
                table: "corrections",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_insight_votes_UserId",
                table: "insight_votes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_actions_AtUtc",
                table: "moderation_actions",
                column: "AtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "community_insights");

            migrationBuilder.DropTable(
                name: "content_flags");

            migrationBuilder.DropTable(
                name: "corrections");

            migrationBuilder.DropTable(
                name: "insight_votes");

            migrationBuilder.DropTable(
                name: "moderation_actions");

            migrationBuilder.DropColumn(
                name: "HiddenReason",
                table: "comments");
        }
    }
}
