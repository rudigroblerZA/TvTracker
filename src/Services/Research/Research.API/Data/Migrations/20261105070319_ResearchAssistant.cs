using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Research.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResearchAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SeasonsAtSave",
                table: "saved_research",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AssistantPreparedAtUtc",
                table: "dossiers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistantReason",
                table: "dossiers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeasonsAtSave",
                table: "saved_research");

            migrationBuilder.DropColumn(
                name: "AssistantPreparedAtUtc",
                table: "dossiers");

            migrationBuilder.DropColumn(
                name: "AssistantReason",
                table: "dossiers");
        }
    }
}
