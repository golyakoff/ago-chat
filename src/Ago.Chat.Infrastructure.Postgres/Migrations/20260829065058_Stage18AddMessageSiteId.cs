using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// `18-01`/`adr/0031` Addendum: denormalizes `site_id` onto the partitioned `messages` table -
    /// nothing else. `ALTER TABLE ... ADD COLUMN` on a partitioned parent is a metadata-only change
    /// that Postgres cascades to every existing leaf partition without a table rewrite (the column is
    /// nullable with no default, so there is nothing to backfill inline), which is why this migration
    /// stays this small.
    ///
    /// <para><b>Deliberately does not create the composite `(site_id, created_at)` index or the
    /// full-text GIN index this column exists to serve, and does not backfill existing rows.</b> Both
    /// would need <c>CREATE INDEX CONCURRENTLY</c> (a full-table lock for the read-heavy `messages`
    /// table is not acceptable, matching `PartitionMaintenanceJob`'s own established stance on
    /// partition DDL), and Postgres refuses to run `CONCURRENTLY` inside any transaction - including
    /// EF's own transaction around a migration's `Up()`, and including a `DO $$ ... $$` block, which
    /// cannot help here because `CONCURRENTLY` is refused inside a function body too, not just an
    /// explicit `BEGIN`. Worse, which leaf partitions exist is a live catalog fact
    /// (`PartitionMaintenanceJob` keeps creating new ones on its own schedule) that `Up(MigrationBuilder)`
    /// has no way to query - it only builds a list of operations for the migrator to run later, with no
    /// database connection of its own. A migration can build a plausible partition list from a clock
    /// computation (`Stage2PartitionMessages`'s own technique), but that list would silently go stale
    /// the day the operational partition count drifts from the assumption, and nothing would ever
    /// notice. Per-partition `CONCURRENTLY` DDL on this table already has an owner that does not have
    /// either problem - a running background service, which holds a real connection to enumerate
    /// `pg_inherits` and never wraps its own statements in an ambient transaction. See
    /// `Ago.Chat.Worker.MessageSearchIndexJob` for the indexes and `Ago.Chat.Worker.
    /// MessageSiteIdBackfillJob` for the backfill - `adr/0073` records this split as the general answer
    /// for any future per-partition DDL on this table, not a one-off worked around here.</para>
    /// </summary>
    public partial class Stage18AddMessageSiteId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "site_id",
                table: "messages",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "site_id",
                table: "messages");
        }
    }
}
