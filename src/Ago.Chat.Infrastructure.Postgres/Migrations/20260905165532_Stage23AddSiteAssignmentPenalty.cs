using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>
    /// `23-05`: one additive column, `sites.assignment_penalty_seconds` (default `120`, matching
    /// <c>Site.AssignmentPenaltySeconds</c>'s own field initialiser - every existing row reads back
    /// `120` with no backfill), plus the check-constraint widening its own first real writer needs in
    /// the same wave - `ConversationAssignmentSource.Additional`'s first writers
    /// (`SkipLockedAssignmentClaimer`'s and `RedisLockAssignmentClaimer`'s own second pass) land in
    /// this same change, unlike `Taken`'s own history (`Stage23WidenConversationAssignmentSourceCheckConstraint`'s
    /// own remarks: writer and constraint were, for one wave, out of step - not repeated here).
    /// No hand-written SQL - every statement below is EF-generated from the model diff.
    /// </summary>
    public partial class Stage23AddSiteAssignmentPenalty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversation_assignments_source",
                table: "conversation_assignments");

            migrationBuilder.AddColumn<int>(
                name: "assignment_penalty_seconds",
                table: "sites",
                type: "integer",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversation_assignments_source",
                table: "conversation_assignments",
                sql: "source IN ('Assigned', 'Transferred', 'Taken', 'Additional')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversation_assignments_source",
                table: "conversation_assignments");

            migrationBuilder.DropColumn(
                name: "assignment_penalty_seconds",
                table: "sites");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversation_assignments_source",
                table: "conversation_assignments",
                sql: "source IN ('Assigned', 'Transferred', 'Taken')");
        }
    }
}
