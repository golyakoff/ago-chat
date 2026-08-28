using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage16AddErasureRequestedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erasure_requested_at",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erasure_requested_at",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_sites_erasure_pending",
                table: "sites",
                column: "erasure_requested_at",
                filter: "erasure_requested_at is not null");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_erasure_pending",
                table: "conversations",
                column: "erasure_requested_at",
                filter: "erasure_requested_at is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sites_erasure_pending",
                table: "sites");

            migrationBuilder.DropIndex(
                name: "ix_conversations_erasure_pending",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "erasure_requested_at",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "erasure_requested_at",
                table: "conversations");
        }
    }
}
