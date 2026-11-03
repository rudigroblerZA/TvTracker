using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Research.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dossiers",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Draft = table.Column<string>(type: "text", nullable: true),
                    DraftingSinceUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DraftProblem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    GatheredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dossiers", x => x.ShowId);
                });

            migrationBuilder.CreateTable(
                name: "fact_candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact_candidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "saved_research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Insight = table.Column<string>(type: "text", nullable: false),
                    Facts = table.Column<string>(type: "jsonb", nullable: false),
                    Sources = table.Column<string>(type: "jsonb", nullable: false),
                    SavedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SavedById = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SavedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_research", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Excerpt = table.Column<string>(type: "text", nullable: false),
                    Included = table.Column<bool>(type: "boolean", nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sources", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fact_candidates_ShowId",
                table: "fact_candidates",
                column: "ShowId");

            migrationBuilder.CreateIndex(
                name: "IX_saved_research_ShowId_Version",
                table: "saved_research",
                columns: new[] { "ShowId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sources_ShowId",
                table: "sources",
                column: "ShowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dossiers");

            migrationBuilder.DropTable(
                name: "fact_candidates");

            migrationBuilder.DropTable(
                name: "saved_research");

            migrationBuilder.DropTable(
                name: "sources");
        }
    }
}
