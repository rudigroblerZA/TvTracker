using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class Plans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "profiles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "free");

            migrationBuilder.AddColumn<DateTime>(
                name: "PlanChangedAtUtc",
                table: "profiles",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Plan",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "PlanChangedAtUtc",
                table: "profiles");
        }
    }
}
