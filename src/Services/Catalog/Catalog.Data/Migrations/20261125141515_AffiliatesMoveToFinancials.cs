using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class AffiliatesMoveToFinancials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "affiliate_conversions");

            migrationBuilder.DropTable(
                name: "affiliate_programs");

            migrationBuilder.DropTable(
                name: "outbound_clicks");

            migrationBuilder.CreateTable(
                name: "affiliate_programs_copy",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Programs = table.Column<string>(type: "jsonb", nullable: false),
                    AsOfUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_affiliate_programs_copy", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "affiliate_programs_copy");

            migrationBuilder.CreateTable(
                name: "affiliate_programs",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PostbackKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Regions = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    Services = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    Template = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Test = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_affiliate_programs", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "outbound_clicks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClickedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Link = table.Column<int>(type: "integer", nullable: false),
                    Plan = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Program = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Region = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Service = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ShowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Surface = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false)
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
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    ClickId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Program = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
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
    }
}
