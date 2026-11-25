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
            // The affiliate tables (affiliate_programs, outbound_clicks, affiliate_conversions) are Financials.API's now
            // and no longer in this model. They are left in place, not dropped: Financials copies their rows into its
            // own database the first time it starts (LegacyImport), and a deployment drops them once it has.
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
        }
    }
}
