using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class DedupeWatchProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TvMaze:WatchRegions was bound onto its default list, so every region was stored twice per service.
            migrationBuilder.Sql("""
                DELETE FROM watch_providers w
                USING watch_providers d
                WHERE w."ShowId" = d."ShowId" AND w."Region" = d."Region" AND w."ProviderName" = d."ProviderName"
                  AND w."Type" = d."Type" AND w."Id" > d."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
