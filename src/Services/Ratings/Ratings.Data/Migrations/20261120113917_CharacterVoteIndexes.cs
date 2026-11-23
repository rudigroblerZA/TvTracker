using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ratings.Data.Migrations
{
    /// <inheritdoc />
    public partial class CharacterVoteIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_character_votes_EpisodeId",
                table: "character_votes");

            migrationBuilder.DropIndex(
                name: "IX_character_votes_ShowId",
                table: "character_votes");

            migrationBuilder.CreateIndex(
                name: "IX_character_votes_EpisodeId_PersonId",
                table: "character_votes",
                columns: new[] { "EpisodeId", "PersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_character_votes_ShowId_PersonId",
                table: "character_votes",
                columns: new[] { "ShowId", "PersonId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_character_votes_EpisodeId_PersonId",
                table: "character_votes");

            migrationBuilder.DropIndex(
                name: "IX_character_votes_ShowId_PersonId",
                table: "character_votes");

            migrationBuilder.CreateIndex(
                name: "IX_character_votes_EpisodeId",
                table: "character_votes",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_character_votes_ShowId",
                table: "character_votes",
                column: "ShowId");
        }
    }
}
