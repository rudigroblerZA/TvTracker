using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class AffiliateClicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbound_clicks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Service = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Link = table.Column<int>(type: "integer", nullable: false),
                    Region = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Surface = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Plan = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Program = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ClickedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_clicks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "affiliate_conversions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClickId = table.Column<Guid>(type: "uuid", nullable: false),
                    Program = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_affiliate_conversions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_affiliate_conversions_outbound_clicks_ClickId",
                        column: x => x.ClickId,
                        principalTable: "outbound_clicks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_affiliate_conversions_ClickId",
                table: "affiliate_conversions",
                column: "ClickId");

            migrationBuilder.CreateIndex(
                name: "IX_affiliate_conversions_Program_TransactionId",
                table: "affiliate_conversions",
                columns: new[] { "Program", "TransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_affiliate_conversions_ReceivedAtUtc",
                table: "affiliate_conversions",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_outbound_clicks_ClickedAtUtc",
                table: "outbound_clicks",
                column: "ClickedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "affiliate_conversions");

            migrationBuilder.DropTable(
                name: "outbound_clicks");
        }
    }
}
