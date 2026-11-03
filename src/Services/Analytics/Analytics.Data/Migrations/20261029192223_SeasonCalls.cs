using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Analytics.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeasonCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "season_calls",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: false),
                    Rating = table.Column<double>(type: "double precision", nullable: false),
                    ModelRating = table.Column<double>(type: "double precision", nullable: true),
                    CalledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Actual = table.Column<double>(type: "double precision", nullable: true),
                    ScoredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_season_calls", x => new { x.UserId, x.ShowId, x.SeasonNumber });
                });

            migrationBuilder.CreateIndex(
                name: "IX_season_calls_ScoredAtUtc",
                table: "season_calls",
                column: "ScoredAtUtc",
                filter: "\"ScoredAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_season_calls_ShowId_SeasonNumber",
                table: "season_calls",
                columns: new[] { "ShowId", "SeasonNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "season_calls");
        }
    }
}
