using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class InboxLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_inbox_UserId_SourceEventId",
                table: "inbox");

            migrationBuilder.CreateIndex(
                name: "IX_inbox_SourceEventId_UserId",
                table: "inbox",
                columns: new[] { "SourceEventId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_ScheduledForUtc",
                table: "deliveries",
                column: "ScheduledForUtc",
                filter: "\"Status\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_inbox_SourceEventId_UserId",
                table: "inbox");

            migrationBuilder.DropIndex(
                name: "IX_deliveries_ScheduledForUtc",
                table: "deliveries");

            migrationBuilder.CreateIndex(
                name: "IX_inbox_UserId_SourceEventId",
                table: "inbox",
                columns: new[] { "UserId", "SourceEventId" },
                unique: true);
        }
    }
}
