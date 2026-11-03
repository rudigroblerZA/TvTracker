using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Analytics.Data.Migrations
{
    /// <inheritdoc />
    public partial class WatchRecordedAtAndCastIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_cast_credits_PersonName",
                table: "cast_credits");

            migrationBuilder.AddColumn<DateTime>(
                name: "RecordedAtUtc",
                table: "user_episode_watches",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Marks copied before the column existed: the watch time is the best guess at when they reached us.
            migrationBuilder.Sql("UPDATE user_episode_watches SET \"RecordedAtUtc\" = \"WatchedAtUtc\"");

            migrationBuilder.CreateIndex(
                name: "IX_cast_credits_PersonId",
                table: "cast_credits",
                column: "PersonId",
                filter: "\"PersonId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_cast_credits_PersonId",
                table: "cast_credits");

            migrationBuilder.DropColumn(
                name: "RecordedAtUtc",
                table: "user_episode_watches");

            migrationBuilder.CreateIndex(
                name: "IX_cast_credits_PersonName",
                table: "cast_credits",
                column: "PersonName");
        }
    }
}
