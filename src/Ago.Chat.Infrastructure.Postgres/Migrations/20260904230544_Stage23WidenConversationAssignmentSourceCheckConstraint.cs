using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23WidenConversationAssignmentSourceCheckConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversation_assignments_source",
                table: "conversation_assignments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversation_assignments_source",
                table: "conversation_assignments",
                sql: "source IN ('Assigned', 'Transferred', 'Taken')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversation_assignments_source",
                table: "conversation_assignments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversation_assignments_source",
                table: "conversation_assignments",
                sql: "source IN ('Assigned', 'Transferred')");
        }
    }
}
