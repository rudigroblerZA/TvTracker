using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class MadeShows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "person_made_shows",
                columns: table => new
                {
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowExternalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PosterUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Premiered = table.Column<DateOnly>(type: "date", nullable: true),
                    Ended = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Rating = table.Column<double>(type: "double precision", nullable: true),
                    Popularity = table.Column<double>(type: "double precision", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Roles = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_made_shows", x => new { x.PersonId, x.ShowExternalId });
                    table.ForeignKey(
                        name: "FK_person_made_shows_people_PersonId",
                        column: x => x.PersonId,
                        principalTable: "people",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_person_made_shows_ShowExternalId",
                table: "person_made_shows",
                column: "ShowExternalId");

            // Careers fetched before this kept no shows; fetching them again (30 people a minute) fills the table.
            migrationBuilder.Sql("""UPDATE people SET "CareerRefreshedAtUtc" = NULL WHERE "CareerRefreshedAtUtc" IS NOT NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "person_made_shows");
        }
    }
}
