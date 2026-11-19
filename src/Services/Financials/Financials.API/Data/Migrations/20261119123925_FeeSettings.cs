using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Financials.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class FeeSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fee_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Fixed = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fee_settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fee_settings");
        }
    }
}
