using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ratings.Data.Migrations
{
    /// <inheritdoc />
    public partial class CharacterVotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "character_votes",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_votes", x => new { x.UserId, x.EpisodeId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_character_votes_EpisodeId",
                table: "character_votes",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_character_votes_ShowId",
                table: "character_votes",
                column: "ShowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "character_votes");
        }
    }
}
