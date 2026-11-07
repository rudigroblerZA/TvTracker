using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class InboxRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_inbox_CreatedAtUtc",
                table: "inbox",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_inbox_ReadAtUtc",
                table: "inbox",
                column: "ReadAtUtc",
                filter: "\"ReadAtUtc\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_inbox_CreatedAtUtc",
                table: "inbox");

            migrationBuilder.DropIndex(
                name: "IX_inbox_ReadAtUtc",
                table: "inbox");
        }
    }
}
