using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class WeeklyDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DigestDay",
                table: "preferences",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DigestHour",
                table: "preferences",
                type: "integer",
                nullable: false,
                defaultValue: 9);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDigestAtUtc",
                table: "preferences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WeeklyDigest",
                table: "preferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigestDay",
                table: "preferences");

            migrationBuilder.DropColumn(
                name: "DigestHour",
                table: "preferences");

            migrationBuilder.DropColumn(
                name: "LastDigestAtUtc",
                table: "preferences");

            migrationBuilder.DropColumn(
                name: "WeeklyDigest",
                table: "preferences");
        }
    }
}
