using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage4AddWaitingQueueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversations_site_id",
                table: "conversations");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_waiting",
                table: "conversations",
                column: "site_id",
                filter: "state = 'Waiting'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversations_waiting",
                table: "conversations");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_site_id",
                table: "conversations",
                column: "site_id");
        }
    }
}
