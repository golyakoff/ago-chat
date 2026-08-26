using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage8AddSiteDemoExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "demo_expires_at",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_sites_demo_expiry",
                table: "sites",
                column: "demo_expires_at",
                filter: "demo_expires_at is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sites_demo_expiry",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "demo_expires_at",
                table: "sites");
        }
    }
}
