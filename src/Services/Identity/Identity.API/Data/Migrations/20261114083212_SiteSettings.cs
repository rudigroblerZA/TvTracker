using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class SiteSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "site_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    AppName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    AccentColor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    Operator = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ContactEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OperatorAddress = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CompanyNumber = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    GoverningLaw = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_settings");
        }
    }
}
