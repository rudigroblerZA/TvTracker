using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ratings.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommentEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EditedAtUtc",
                table: "comments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditedAtUtc",
                table: "comments");
        }
    }
}
