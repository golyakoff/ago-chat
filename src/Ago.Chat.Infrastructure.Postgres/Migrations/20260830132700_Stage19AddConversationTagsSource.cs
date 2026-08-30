using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage19AddConversationTagsSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "conversation_tags",
                type: "text",
                nullable: false,
                defaultValue: "Operator");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversation_tags_source",
                table: "conversation_tags",
                sql: "source IN ('Operator', 'Ai')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversation_tags_source",
                table: "conversation_tags");

            migrationBuilder.DropColumn(
                name: "source",
                table: "conversation_tags");
        }
    }
}
