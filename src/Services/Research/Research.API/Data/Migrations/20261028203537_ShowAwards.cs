using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Research.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShowAwards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "show_awards",
                columns: table => new
                {
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PosterUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Wins = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    WikidataUrl = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_show_awards", x => x.ShowId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_show_awards_Wins",
                table: "show_awards",
                column: "Wins");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "show_awards");
        }
    }
}
