using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddConversationAttachmentBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "attachment_bytes_reserved",
                table: "conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attachment_bytes_reserved",
                table: "conversations");
        }
    }
}
