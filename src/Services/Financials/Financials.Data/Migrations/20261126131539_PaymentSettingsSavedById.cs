using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Financials.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentSettingsSavedById : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UpdatedById",
                table: "payment_settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedById",
                table: "payment_settings");
        }
    }
}
