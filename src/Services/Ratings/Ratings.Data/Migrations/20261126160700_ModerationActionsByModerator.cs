using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ratings.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModerationActionsByModerator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_moderation_actions_ModeratorId",
                table: "moderation_actions",
                column: "ModeratorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_moderation_actions_ModeratorId",
                table: "moderation_actions");
        }
    }
}
