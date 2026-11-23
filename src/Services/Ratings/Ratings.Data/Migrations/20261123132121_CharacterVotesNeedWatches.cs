using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ratings.Data.Migrations
{
    /// <inheritdoc />
    public partial class CharacterVotesNeedWatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Votes left behind by watches that went before the two were tied together: the key can't be added over them.
            migrationBuilder.Sql("""
                delete from character_votes v where not exists (
                    select 1 from watched_episodes w where w."UserId" = v."UserId" and w."EpisodeId" = v."EpisodeId")
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_character_votes_watched_episodes_UserId_EpisodeId",
                table: "character_votes",
                columns: new[] { "UserId", "EpisodeId" },
                principalTable: "watched_episodes",
                principalColumns: new[] { "UserId", "EpisodeId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_character_votes_watched_episodes_UserId_EpisodeId",
                table: "character_votes");
        }
    }
}
