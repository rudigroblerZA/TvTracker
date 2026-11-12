using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            // A name as search compares it: lower case, accents removed. unaccent() alone is only STABLE (its dictionary
            // could change), so it can't be indexed; naming the dictionary in an IMMUTABLE wrapper is the usual way round.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION search_text(text) RETURNS text
                    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
                    AS $$ SELECT lower(public.unaccent('public.unaccent', $1)) $$;
                """);
            // Trigram indexes on it, for "contains" and near-spelling matches on shows' titles and people's names.
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_shows_Title_search" ON shows USING gin (search_text("Title") gin_trgm_ops);""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_people_Name_search" ON people USING gin (search_text("Name") gin_trgm_ops);""");

            // This replaces two earlier migrations (SearchTrigrams, SearchText) that were never released. A development
            // database that ran them already has the function and indexes (hence "if not exists" above), and may have
            // the first one's indexes on the bare columns, which nothing uses.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_shows_Title_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_people_Name_trgm";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_people_Name_search";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_shows_Title_search";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS search_text(text);");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
