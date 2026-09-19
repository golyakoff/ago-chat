using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>
    /// `25-170`: "stop treating 'holds a seat' as a fact about an `Operator` account, and start treating
    /// it as a fact about one `(operator account, role)` pairing." `operator_roles` gains `holds_seat`/
    /// `granted_at`; `operators.holds_seat` is dropped once both new columns are correctly backfilled
    /// from it (never before - the hand-edited ordering below, not EF's own scaffolded order, which drops
    /// the source column before anything reads it).
    ///
    /// <para><b>Backfill, not a blanket default alone.</b> Every row gets `holds_seat = true` and
    /// `granted_at = '0001-01-01T00:00:00Z'` (the column's own default) as a starting point - correct for
    /// every Admin-role row today (`AdministratorLimitEnforcer`'s own now-retired reasoning: "nothing
    /// today has ever disabled one") and for `granted_at` on any row this migration cannot otherwise date.
    /// Two `UPDATE`s then correct what the blanket default gets wrong:
    /// <list type="number">
    /// <item>Every Operator-role row's own `holds_seat` is overwritten from the departing
    /// `operators.holds_seat` value it is actually replacing - a real operator disabled via
    /// `ToggleOperatorSeatHandler` before this migration must not silently regain their seat.</item>
    /// <item>Every row with a real promotion record in `role_change_records` gets that record's own
    /// `changed_at` as `granted_at` - the identical signal `AdministratorLimitEnforcer`'s own retired
    /// tie-break already read for "most recently promoted." A row with no such record (a founder seeded
    /// both roles at registration, or an operator invited directly into a role) keeps the
    /// `0001-01-01` sentinel - deliberately the oldest possible value, not a guess, so the reconciliation
    /// procedure's own "most-recently-granted-first" order ranks every real promotion ahead of every
    /// un-recorded grant, the identical ranking `AdministratorLimitEnforcer`'s own remarks already
    /// established and this migration is careful not to silently invert.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>One-way in spirit past the point `operators.holds_seat` is dropped</b> - `Down()` restores
    /// the column (`true` for every row, `dotnet ef`'s own scaffolded default) but cannot restore
    /// per-operator history the drop discarded; reversible in the schema sense the tooling checks, not a
    /// promise this migration's own `Down()` recovers a disabled seat correctly. No production data exists
    /// for this deployment yet (`CLAUDE.md`'s own portfolio-project framing), so this is stated for the
    /// record rather than mitigated further.</para>
    /// </summary>
    public partial class Stage25AddOperatorRoleSeatColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "granted_at",
                table: "operator_roles",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<bool>(
                name: "holds_seat",
                table: "operator_roles",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "entitlement_paused_at",
                table: "channel_credentials",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill 1: every Operator-role row's own holds_seat carries forward from the operator
            // account's own departing flag - the fact this column is replacing, not the blanket
            // "true" default every row (Admin-role rows included) otherwise keeps.
            migrationBuilder.Sql("""
                UPDATE operator_roles orl
                SET holds_seat = o.holds_seat
                FROM operators o, roles r
                WHERE orl.operator_id = o.id
                  AND orl.role_id = r.id
                  AND r.site_id = o.site_id
                  AND r.name = 'Operator';
                """);

            // Backfill 2: every (operator, role) pairing with a real promotion record in
            // role_change_records gets that record's own most-recent changed_at - see this migration's
            // own class-level remarks for why an un-recorded row (founder, direct invite) deliberately
            // keeps the sentinel default instead.
            migrationBuilder.Sql("""
                UPDATE operator_roles orl
                SET granted_at = sub.last_changed_at
                FROM (
                    SELECT rcr.changed_operator_id AS operator_id, r.id AS role_id, MAX(rcr.changed_at) AS last_changed_at
                    FROM role_change_records rcr
                    JOIN roles r ON r.site_id = rcr.site_id AND r.name = rcr.new_role_name
                    GROUP BY rcr.changed_operator_id, r.id
                ) sub
                WHERE orl.operator_id = sub.operator_id AND orl.role_id = sub.role_id;
                """);

            // Only now, once every operator_roles row correctly reflects what operators.holds_seat used
            // to say, is the source column dropped - `Operator.HoldsSeat`'s own removal made real.
            migrationBuilder.DropColumn(
                name: "holds_seat",
                table: "operators");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "holds_seat",
                table: "operators",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.DropColumn(
                name: "granted_at",
                table: "operator_roles");

            migrationBuilder.DropColumn(
                name: "holds_seat",
                table: "operator_roles");

            migrationBuilder.DropColumn(
                name: "entitlement_paused_at",
                table: "channel_credentials");
        }
    }
}
