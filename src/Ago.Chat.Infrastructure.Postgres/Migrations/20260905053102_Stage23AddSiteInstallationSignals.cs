using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddSiteInstallationSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_seen_at",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_refused_origin",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_refused_origin_at",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_seen_at",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "first_seen_at",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "last_refused_origin",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "last_refused_origin_at",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "sites");
        }
    }
}
