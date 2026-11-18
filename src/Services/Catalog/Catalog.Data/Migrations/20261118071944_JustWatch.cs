using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class JustWatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "watch_providers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ImdbId",
                table: "shows",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "justwatch_listings",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Region = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Found = table.Column<bool>(type: "boolean", nullable: false),
                    Path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_justwatch_listings", x => new { x.ShowId, x.Region });
                    table.ForeignKey(
                        name: "FK_justwatch_listings_shows_ShowId",
                        column: x => x.ShowId,
                        principalTable: "shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_justwatch_listings_CheckedAtUtc",
                table: "justwatch_listings",
                column: "CheckedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "justwatch_listings");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "watch_providers");

            migrationBuilder.DropColumn(
                name: "ImdbId",
                table: "shows");
        }
    }
}
