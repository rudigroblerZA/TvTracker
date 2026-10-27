using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyStateAndLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Private",
                table: "profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Approved",
                table: "follows",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AirsAtUtc",
                table: "episodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "blocks",
                columns: table => new
                {
                    BlockerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BlockedId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blocks", x => new { x.BlockerId, x.BlockedId });
                });

            migrationBuilder.CreateTable(
                name: "shared_lists",
                columns: table => new
                {
                    ListId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ShowIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_lists", x => x.ListId);
                });

            migrationBuilder.CreateTable(
                name: "user_shows",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Followed = table.Column<bool>(type: "boolean", nullable: false),
                    FollowedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Stars = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_shows", x => new { x.UserId, x.ShowId });
                });

            migrationBuilder.CreateTable(
                name: "user_watches",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    WatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_watches", x => new { x.UserId, x.EpisodeId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_blocks_BlockedId",
                table: "blocks",
                column: "BlockedId");

            migrationBuilder.CreateIndex(
                name: "IX_shared_lists_UserId",
                table: "shared_lists",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_shows_ShowId",
                table: "user_shows",
                column: "ShowId");

            migrationBuilder.CreateIndex(
                name: "IX_user_watches_ShowId_UserId",
                table: "user_watches",
                columns: new[] { "ShowId", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocks");

            migrationBuilder.DropTable(
                name: "shared_lists");

            migrationBuilder.DropTable(
                name: "user_shows");

            migrationBuilder.DropTable(
                name: "user_watches");

            migrationBuilder.DropColumn(
                name: "Private",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "Approved",
                table: "follows");

            migrationBuilder.DropColumn(
                name: "AirsAtUtc",
                table: "episodes");
        }
    }
}
