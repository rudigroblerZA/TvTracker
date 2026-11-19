using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ratings.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommentGifs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GifId",
                table: "comments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GifId",
                table: "comments");
        }
    }
}
