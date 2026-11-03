using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class Families : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EffectivePlan",
                table: "profiles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "free");

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectivePlanChangedAtUtc",
                table: "profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "families",
                columns: table => new
                {
                    OwnerUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InviteToken = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_families", x => x.OwnerUserId);
                });

            migrationBuilder.CreateTable(
                name: "family_members",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_family_members", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_families_InviteToken",
                table: "families",
                column: "InviteToken",
                unique: true,
                filter: "\"InviteToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_family_members_OwnerUserId",
                table: "family_members",
                column: "OwnerUserId");

            // Everyone's effective plan starts as the plan they pay for; no families exist yet.
            migrationBuilder.Sql("UPDATE profiles SET \"EffectivePlan\" = \"Plan\", \"EffectivePlanChangedAtUtc\" = \"PlanChangedAtUtc\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "families");

            migrationBuilder.DropTable(
                name: "family_members");

            migrationBuilder.DropColumn(
                name: "EffectivePlan",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "EffectivePlanChangedAtUtc",
                table: "profiles");
        }
    }
}
