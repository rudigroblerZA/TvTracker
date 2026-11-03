using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonCastCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CastCreditsRefreshedAtUtc",
                table: "people",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "person_cast_credits",
                columns: table => new
                {
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowExternalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PosterUrl = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Premiered = table.Column<DateOnly>(type: "date", nullable: true),
                    Popularity = table.Column<double>(type: "double precision", nullable: false),
                    Rating = table.Column<double>(type: "double precision", nullable: true),
                    Character = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Voice = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_cast_credits", x => new { x.PersonId, x.ShowExternalId });
                    table.ForeignKey(
                        name: "FK_person_cast_credits_people_PersonId",
                        column: x => x.PersonId,
                        principalTable: "people",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "person_cast_credits");

            migrationBuilder.DropColumn(
                name: "CastCreditsRefreshedAtUtc",
                table: "people");
        }
    }
}
