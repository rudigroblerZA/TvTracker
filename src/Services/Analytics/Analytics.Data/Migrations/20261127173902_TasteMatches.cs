using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Analytics.Data.Migrations
{
    /// <inheritdoc />
    public partial class TasteMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "taste_matches",
                columns: table => new
                {
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InviterUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InviterDisplayName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FriendUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FriendDisplayName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GuestPicks = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    GuestKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    InviterUnnamed = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    FriendUnnamed = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Result = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DoneAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_taste_matches", x => x.Token);
                });

            migrationBuilder.CreateIndex(
                name: "IX_taste_matches_ExpiresAtUtc",
                table: "taste_matches",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_taste_matches_FriendUserId",
                table: "taste_matches",
                column: "FriendUserId",
                filter: "\"FriendUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_taste_matches_InviterUserId",
                table: "taste_matches",
                column: "InviterUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "taste_matches");
        }
    }
}
