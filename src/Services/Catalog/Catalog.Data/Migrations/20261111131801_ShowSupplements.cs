using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShowSupplements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "show_supplements",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Item = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Overview = table.Column<string>(type: "text", nullable: true),
                    Network = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FirstAirDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PosterUrl = table.Column<string>(type: "text", nullable: true),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_show_supplements", x => x.ShowId);
                    table.ForeignKey(
                        name: "FK_show_supplements_shows_ShowId",
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
                name: "show_supplements");
        }
    }
}
