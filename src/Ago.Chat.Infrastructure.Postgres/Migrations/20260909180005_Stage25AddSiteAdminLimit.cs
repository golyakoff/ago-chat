using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>
    /// `25-25`: the second, independent seat-shaped limit `ago-business` decision `0011` describes -
    /// `Site` carried exactly one (`Stage13AddSiteTierAndSeatLimit`'s own `seat_limit`), and every
    /// seat-holding role, Operator or Administrator alike, was gated against it identically. This
    /// column is Administrator's own ceiling, read by `OperatorInviteRedemptionRepository` (an
    /// Administrator invite) and `ChangeOperatorRoleHandler` (promoting an existing colleague) exactly
    /// the way `seat_limit` already gates an Operator invite and a seat toggle.
    ///
    /// <para><b>The database default (`1`) is the free tier's own number, and this migration also
    /// backfills every already-paid-tier row to `2`</b> - the identical two-step shape
    /// `Stage13RaiseFreeTierSeatLimit` already used for `seat_limit`'s own default change, for the
    /// identical reason: a database default only ever applies to a row inserted <em>after</em> this
    /// migration runs, so every row already in `sites` needs the explicit `UPDATE` below to see the
    /// tier it is actually on. `Site.ActivateSubscription`/the constructor both derive this value from
    /// `Tier` from this point forward (`SubscriptionTierBands.ResolveAdminLimit`) - this backfill is
    /// only for the rows that predate either ever having a chance to.</para>
    /// </summary>
    public partial class Stage25AddSiteAdminLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "admin_limit",
                table: "sites",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // The backfill. `tier <> 'free' AND admin_limit < 2` rather than an unconditional
            // `tier <> 'free'` - idempotent (a rerun, or a row already at 2 for some other reason, is
            // left untouched) and scoped to exactly the rows the column default's own value cannot
            // reach: every row already in `sites` before this migration ran, on whatever paid tier it
            // was already on, still reads the free-tier default of `1` until this statement runs.
            migrationBuilder.Sql("UPDATE sites SET admin_limit = 2 WHERE tier <> 'free' AND admin_limit < 2;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately no data revert here, the identical "do not invent a fact" grounds
            // `Stage13RaiseFreeTierSeatLimit`'s own `Down` already states for the identical situation -
            // reverting every paid-tier row from 2 back to 1 could strand a site that already appointed
            // a second administrator in the meantime.
            migrationBuilder.DropColumn(
                name: "admin_limit",
                table: "sites");
        }
    }
}
