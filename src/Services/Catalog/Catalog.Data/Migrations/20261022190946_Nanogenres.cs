using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class Nanogenres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "Nanogenres",
                table: "shows",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateTable(
                name: "nanogenres",
                columns: table => new
                {
                    Slug = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ShowCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nanogenres", x => x.Slug);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shows_Nanogenres",
                table: "shows",
                column: "Nanogenres")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nanogenres");

            migrationBuilder.DropIndex(
                name: "IX_shows_Nanogenres",
                table: "shows");

            migrationBuilder.DropColumn(
                name: "Nanogenres",
                table: "shows");
        }
    }
}
