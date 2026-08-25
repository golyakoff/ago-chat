using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage12AddSiteCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // `12-02`: additive and reversible, no table rewrite. Deliberately nullable with no
            // default and no backfill: every row that already exists genuinely has no known creation
            // time, and `defaultValueSql: "now()"` would stamp all of them with the instant this
            // migration ran and present that as fact (`CLAUDE.md`: do not invent numbers).
            // `Ago.Chat.Domain.Site.CreatedAt` carries the full reasoning; the owner overview renders
            // those rows as `null`.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "created_at",
                table: "sites");
        }
    }
}
