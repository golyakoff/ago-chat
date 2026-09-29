using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage26AddVisitorErasureRequestedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erasure_requested_at",
                table: "visitors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_visitors_erasure_pending",
                table: "visitors",
                column: "erasure_requested_at",
                filter: "erasure_requested_at is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_visitors_erasure_pending",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "erasure_requested_at",
                table: "visitors");
        }
    }
}
