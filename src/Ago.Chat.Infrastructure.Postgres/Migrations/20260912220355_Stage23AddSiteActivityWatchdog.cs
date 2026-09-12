using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddSiteActivityWatchdog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inactivity_warning_sent_at",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_operator_activity_at",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_sites_inactivity_watchdog",
                table: "sites",
                column: "last_operator_activity_at",
                filter: "erasure_requested_at is null");

            // `23-73`: backfill every pre-existing row's watchdog to "now" (the migration's own apply
            // time) rather than leaving it null. Null would read, to InactivityWatchdogQuery, as
            // "never had any recorded activity" - and every site that existed before this feature
            // shipped would then already be inside (or past) its three-month window the instant this
            // migration runs, which is exactly the hazard this backlog item's own Done-when names
            // ("shown not deleting an account whose widget is in active use"). Starting every
            // existing site's own clock fresh at rollout is the same choice `10-02`'s own Name column
            // backfill made for a different column: additive, no attempt to reconstruct a history this
            // deployment never recorded. This is a one-time data migration (this statement runs once,
            // at apply time), not an ongoing default - unlike CreatedAt's own deliberate absence of
            // `HasDefaultValueSql("now()")` (that column's own remarks on why an ongoing DB-clock
            // default would violate `CLAUDE.md` rule 11), there is no "ongoing" here: this SQL executes
            // exactly once, during this migration's own Up(), and every row it touches is genuinely
            // history that predates the column existing at all.
            migrationBuilder.Sql(
                "update sites set last_operator_activity_at = now() where last_operator_activity_at is null;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sites_inactivity_watchdog",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "inactivity_warning_sent_at",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "last_operator_activity_at",
                table: "sites");
        }
    }
}
