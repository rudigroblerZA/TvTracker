using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShowNamesakes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "show_namesake_lookups",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Country = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_show_namesake_lookups", x => x.ShowId);
                    table.ForeignKey(
                        name: "FK_show_namesake_lookups_shows_ShowId",
                        column: x => x.ShowId,
                        principalTable: "shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "show_namesakes",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PosterUrl = table.Column<string>(type: "text", nullable: true),
                    Premiered = table.Column<DateOnly>(type: "date", nullable: true),
                    Ended = table.Column<DateOnly>(type: "date", nullable: true),
                    Network = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Country = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Language = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Popularity = table.Column<double>(type: "double precision", nullable: false),
                    Rating = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_show_namesakes", x => new { x.ShowId, x.ExternalId });
                    table.ForeignKey(
                        name: "FK_show_namesakes_shows_ShowId",
                        column: x => x.ShowId,
                        principalTable: "shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "show_namesake_lookups");

            migrationBuilder.DropTable(
                name: "show_namesakes");
        }
    }
}
