using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shows_Title_trgm",
                table: "shows");

            migrationBuilder.DropIndex(
                name: "IX_people_Name_trgm",
                table: "people");

            // A name as search compares it: lower case, accents removed. unaccent() alone is only STABLE (its dictionary
            // could change), so it can't be indexed; naming the dictionary in an IMMUTABLE wrapper is the usual way round.
            migrationBuilder.Sql("""
                CREATE FUNCTION search_text(text) RETURNS text
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
                    AS $$ SELECT lower(public.unaccent('public.unaccent', $1)) $$;
                """);
            migrationBuilder.Sql("""CREATE INDEX "IX_shows_Title_search" ON shows USING gin (search_text("Title") gin_trgm_ops);""");
            migrationBuilder.Sql("""CREATE INDEX "IX_people_Name_search" ON people USING gin (search_text("Name") gin_trgm_ops);""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX "IX_people_Name_search";""");
            migrationBuilder.Sql("""DROP INDEX "IX_shows_Title_search";""");
            migrationBuilder.Sql("DROP FUNCTION search_text(text);");

            migrationBuilder.CreateIndex(
                name: "IX_shows_Title_trgm",
                table: "shows",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_people_Name_trgm",
                table: "people",
                column: "Name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }
    }
}
