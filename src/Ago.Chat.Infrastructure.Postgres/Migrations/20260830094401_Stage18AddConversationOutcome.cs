using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage18AddConversationOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "outcome",
                table: "conversations",
                type: "text",
                nullable: false,
                defaultValue: "Unset");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_outcome",
                table: "conversations",
                sql: "outcome IN ('Unset', 'Converted', 'NotConverted', 'FollowUpNeeded')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_outcome",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "outcome",
                table: "conversations");
        }
    }
}
