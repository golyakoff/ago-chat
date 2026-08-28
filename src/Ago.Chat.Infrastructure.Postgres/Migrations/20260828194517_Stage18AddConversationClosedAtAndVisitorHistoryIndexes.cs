using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage18AddConversationClosedAtAndVisitorHistoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversations_visitor_id",
                table: "conversations");

            migrationBuilder.RenameIndex(
                name: "IX_channel_identities_visitor_id",
                table: "channel_identities",
                newName: "ix_channel_identities_visitor_id");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_conversations_visitor_all",
                table: "conversations",
                columns: new[] { "visitor_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversations_visitor_all",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "closed_at",
                table: "conversations");

            migrationBuilder.RenameIndex(
                name: "ix_channel_identities_visitor_id",
                table: "channel_identities",
                newName: "IX_channel_identities_visitor_id");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_visitor_id",
                table: "conversations",
                column: "visitor_id");
        }
    }
}
