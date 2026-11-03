using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Research.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class CatalogChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_checks",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Field = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ShowTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CatalogValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    WikidataValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WikidataUrl = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_checks", x => new { x.ShowId, x.Field });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_checks");
        }
    }
}
