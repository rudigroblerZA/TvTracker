using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tracking.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Shared",
                table: "lists",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Shared",
                table: "lists");
        }
    }
}
