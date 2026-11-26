using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.Data.Migrations
{
    /// <inheritdoc />
    public partial class WatchRecordedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RecordedAtUtc",
                table: "user_watches",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // The watches kept so far have only their watch time, which is what snapshots went by until now.
            migrationBuilder.Sql("""update user_watches set "RecordedAtUtc" = "WatchedAtUtc";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecordedAtUtc",
                table: "user_watches");
        }
    }
}
