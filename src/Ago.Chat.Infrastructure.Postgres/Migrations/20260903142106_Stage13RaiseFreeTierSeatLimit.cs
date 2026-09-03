using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>
    /// `13-08`: the free tier's own seat allowance rises from one to two - "the free tier is two
    /// operators with two months of history," matching Jivo's published free plan. Unlike
    /// `Stage13AddSiteTierAndSeatLimit` (an additive column with a database default, no existing row
    /// affected), this is a genuine data migration: the live database holds 17 sites and 20 operators,
    /// every site still on the old `seat_limit = 1` default, so raising only the column's default would
    /// leave every one of them exactly as constrained as before this item shipped - the defect this
    /// item's own backlog names explicitly ("a free-tier site that stays at one seat after this ships is
    /// the defect this item exists to prevent").
    /// </summary>
    public partial class Stage13RaiseFreeTierSeatLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "seat_limit",
                table: "sites",
                type: "integer",
                nullable: false,
                defaultValue: 2,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 1);

            // The backfill. `tier = 'free' AND seat_limit < 2` rather than an unconditional
            // `tier = 'free'` - idempotent (a rerun, or a row already at 2 for some other reason, is
            // left untouched) and scoped to exactly the rows the column default's own change cannot
            // reach: a database default only ever applies to a row inserted *after* this migration, so
            // every row already in `sites` needs this statement to actually see the new allowance.
            // Paid-tier rows are never touched - ActivateSubscription already writes a seat_limit of at
            // least SubscriptionTierBands.MinSeats (3, this same item), so no paid row could ever match
            // this predicate.
            migrationBuilder.Sql("UPDATE sites SET seat_limit = 2 WHERE tier = 'free' AND seat_limit < 2;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "seat_limit",
                table: "sites",
                type: "integer",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 2);

            // Deliberately no data revert here, on the same "do not invent a fact" grounds CLAUDE.md
            // states elsewhere in this schema (Site.CreatedAt's own remarks on why a backfill was
            // refused). Reverting every free-tier row from 2 back to 1 would silently break any site
            // that used its second seat in the meantime - an invited operator sitting on a site whose
            // seat_limit just dropped below its own operator count is a worse state than the one before
            // this migration, not a faithful undo of it. This Down only reverts the column's own default
            // for rows inserted after a rollback; it does not, and must not, touch data.
        }
    }
}
