using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tracking.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkToken",
                table: "lists",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_lists_LinkToken",
                table: "lists",
                column: "LinkToken",
                unique: true,
                filter: "\"LinkToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_lists_LinkToken",
                table: "lists");

            migrationBuilder.DropColumn(
                name: "LinkToken",
                table: "lists");
        }
    }
}
