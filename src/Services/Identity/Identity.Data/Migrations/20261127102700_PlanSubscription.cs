using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlanSubscription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EndedSubscriptionAtUtc",
                table: "profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EndedSubscriptionId",
                table: "profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlanSubscriptionId",
                table: "profiles",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndedSubscriptionAtUtc",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "EndedSubscriptionId",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "PlanSubscriptionId",
                table: "profiles");
        }
    }
}
