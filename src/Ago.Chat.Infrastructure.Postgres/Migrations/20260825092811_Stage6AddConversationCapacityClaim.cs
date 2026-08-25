using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>
    /// `6-09`: <c>conversations.holds_capacity_claim</c> - the receipt for an
    /// <c>operators.active_chats</c> slot taken by the automatic assignment engine, so closing a
    /// conversation can hand back exactly the claim it holds and nothing else (see
    /// <c>Conversation.HoldsCapacityClaim</c>).
    ///
    /// <para><b>This migration also repairs already-wrong rows</b>, which a schema migration normally
    /// has no business doing. It is done here deliberately: every environment that has ever run the
    /// pre-`6-09` code has an <c>active_chats</c> that only ever went up, and on the local dev
    /// database and the public demo both seeded operators are pinned at <c>active_chats = capacity</c>
    /// with dozens of conversations stuck in `Waiting` behind them - the assignment engine correctly
    /// refusing to assign anything to an operator that, as far as the counter knows, is full. Shipping
    /// the fix without the repair would fix the leak going forward and leave every existing deployment
    /// exactly as jammed as it is now, because nothing in the new code path ever revisits a
    /// conversation that was already closed. A migration is the right vehicle rather than a one-off
    /// script precisely because that repair has to happen on every environment, once, in a known order
    /// relative to the code that depends on it - which is what migrations are for.</para>
    ///
    /// <para><b>What the repair asserts, and why it is not a guess.</b> It does not try to recover
    /// which historical assignments were engine-made - that information does not exist anywhere, which
    /// is the whole reason the new column had to be added. Instead it establishes the invariant as
    /// true at this boundary and lets the new code maintain it from here: every conversation that is
    /// `Assigned` right now is declared to hold a claim, and every operator's <c>active_chats</c> is
    /// set to exactly how many of those they hold. Both halves have to run together - marking the
    /// receipts without resetting the counter would double-count, and resetting the counter without
    /// the receipts would leave the same conversations unable to give their slots back on close. The
    /// result is internally consistent under both regimes: those grandfathered assignments hold one
    /// real slot each and release it on close, while assignments made from here on hold a slot only if
    /// the engine actually took one.</para>
    ///
    /// <para>Deliberately not reversible in <c>Down</c> beyond dropping the column: the numbers this
    /// overwrites are the wrong ones, and there is nothing worth restoring.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class Stage6AddConversationCapacityClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "holds_capacity_claim",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """
                UPDATE conversations
                SET holds_capacity_claim = true
                WHERE state = 'Assigned' AND operator_id IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                UPDATE operators o
                SET active_chats = COALESCE(
                    (SELECT count(*) FROM conversations c
                     WHERE c.operator_id = o.id AND c.state = 'Assigned'), 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "holds_capacity_claim",
                table: "conversations");
        }
    }
}
