using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Analytics.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryAndPeople : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discovery",
                columns: table => new
                {
                    List = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Genre = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PosterUrl = table.Column<string>(type: "text", nullable: true),
                    ShowExternalRating = table.Column<double>(type: "double precision", nullable: true),
                    Value = table.Column<double>(type: "double precision", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "integer", nullable: true),
                    EpisodeTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery", x => new { x.List, x.Genre, x.Rank });
                });

            migrationBuilder.CreateTable(
                name: "person_insights",
                columns: table => new
                {
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_insights", x => x.PersonId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discovery");

            migrationBuilder.DropTable(
                name: "person_insights");
        }
    }
}
