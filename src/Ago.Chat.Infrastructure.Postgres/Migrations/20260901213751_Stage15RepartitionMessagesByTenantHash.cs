using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// `15-09`/`adr/0087`: `messages` moves from `PARTITION BY LIST (retention_class)` -> `PARTITION BY
    /// RANGE (created_at)` (two levels, `13-06`) to `PARTITION BY HASH (site_id)`, 64 fixed buckets, no
    /// time dimension. `retention_class` leaves the partition key and becomes an ordinary column - it
    /// still decides how long a message lives, it no longer decides where the row is stored.
    ///
    /// <para>Postgres cannot convert a partitioned table's own partition scheme in place any more than
    /// it could partition a plain table in place (`Stage2PartitionMessages`'s own remarks, restated once
    /// per repartitioning since) - the only path is the identical rename/create/copy/drop technique:
    /// rename the two-level table out of the way, build the flat 64-bucket replacement, copy every row
    /// across, drop the old table (which drops both its own partition levels with it - Postgres drops a
    /// partitioned table's whole subtree, verified against a real Postgres 17 while building `13-06` and
    /// unchanged since), then re-add the primary key, foreign key, both unique indexes and the
    /// `14-06`/`ck_messages_content_length` CHECK under their original names (renaming a table does not
    /// rename what belongs to it, so the old table's own constraints have to be gone first).</para>
    ///
    /// <para><b>`site_id` closes its historical nullable gap in the same copy, and becomes `NOT NULL`.</b>
    /// Before this item the column was nullable "for history" (`18-01`'s own migration added it without
    /// backfilling every existing row, closed only by the now-deleted `MessageSiteIdBackfillJob`'s slow,
    /// asynchronous convergence). `PARTITION BY HASH (site_id)` makes that gap actively harmful rather
    /// than merely incomplete: a `NULL` `site_id` is technically routable to *some* bucket (Postgres hash
    /// partitioning does not require a `DEFAULT` partition the way `RANGE`/`LIST` do), but a row parked
    /// there is permanently unreachable by any of the `site_id`-scoped queries this whole item exists to
    /// make fast - silent, not structural, data loss from the product's point of view. Since this
    /// migration already joins every row back to `conversations` for defensive reasons (see below), the
    /// same join closes the gap for good: `COALESCE(m.site_id, c.site_id)`, the identical shape `13-06`'s
    /// own migration used to backfill `retention_class`, except this one is not an approximation -
    /// `conversation_id -&gt; conversations.site_id` is an exact join, never a guess. `Message.SiteId`'s own
    /// remarks have the full reasoning and the domain-level consequence (the CLR property is no longer
    /// nullable either).</para>
    ///
    /// <para><b>The `LEFT JOIN` (not `INNER JOIN`) to `conversations` is deliberate defence-in-depth, not
    /// an expected path.</b> `messages.conversation_id` carries a required, cascading foreign key, so an
    /// orphaned message should be structurally impossible - but an `INNER JOIN` would silently *drop* such
    /// a row from the copy if one ever existed (a quiet, undetectable data loss), while a `LEFT JOIN` feeding
    /// a `NOT NULL` column lets Postgres itself refuse the migration loudly if the impossible ever turns out
    /// to be possible. Loud failure over silent wrong data, the same principle `13-06`'s own
    /// `COALESCE(s.tier, 'free')` fallback served for a different column.</para>
    ///
    /// <para><b>The two supporting indexes - composite `(site_id, created_at)` and the full-text GIN on
    /// `body` - move into this migration instead of staying a recurring background job
    /// (`MessageSearchIndexJob`, deleted in this same change), but per bucket, not against the
    /// partitioned parent.</b> That job existed for two reasons: the old scheme's partition list was
    /// dynamic (`PartitionMaintenanceJob` kept creating new monthly leaves), and `CREATE INDEX
    /// CONCURRENTLY` cannot run inside a transaction, which an EF migration's `Up()` normally is. The
    /// first reason is gone under `15-09` - 64 buckets, fixed forever. <b>The second correction, found by
    /// actually running this migration rather than assumed: Postgres flatly refuses `CREATE INDEX
    /// CONCURRENTLY` directly on a *partitioned* table at all - "cannot create index on partitioned table
    /// ... concurrently" - independent of any transaction.</b> An earlier version of this migration tried
    /// exactly that (one statement against the `messages` parent, on the mistaken belief that Postgres
    /// 14+ propagates a concurrently-built parent index to every partition the way a plain, non-concurrent
    /// `CREATE INDEX` on a partitioned table does) and it failed at migration time against a real
    /// Postgres 17. PostgreSQL's own documented workaround is what this migration does instead: build
    /// each of the 64 buckets' own local index `CONCURRENTLY`, one statement per bucket per index (128
    /// statements total) - no parent-level rolled-up index is created, since nothing in this codebase
    /// ever addresses one by name at the `messages` level and a partition's own local index serves the
    /// planner exactly as well unattached. `MigrationBuilder.Sql(sql, suppressTransaction: true)` is what
    /// makes each individual statement legal inside an EF migration - it runs outside the ambient
    /// transaction the rest of `Up()` uses, which `CONCURRENTLY` requires - and why the whole loop is the
    /// last thing in this method: every bucket must already exist before any of its indexes can be
    /// built.</para>
    /// </summary>
    public partial class Stage15RepartitionMessagesByTenantHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE messages RENAME TO messages_pre_hash_partitioning;");

            migrationBuilder.Sql("""
                CREATE TABLE messages (
                    id uuid NOT NULL,
                    conversation_id uuid NOT NULL,
                    sequence integer NOT NULL,
                    author_kind text NOT NULL,
                    author_id uuid NOT NULL,
                    body text NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    retention_class text NOT NULL,
                    site_id uuid NOT NULL,
                    attachment_id uuid NULL,
                    client_message_id uuid NULL,
                    content_kind character varying(64) NULL,
                    content text NULL,
                    actions text NULL
                ) PARTITION BY HASH (site_id);
                """);

            // 64 fixed buckets, created once, never again - MessagePartitionNames.AllBucketNames is
            // the same enumeration Ago.Chat.Worker's retention/archive jobs iterate at runtime, so the
            // names created here and the names those jobs address are guaranteed to agree.
            for (var bucket = 0; bucket < MessagePartitionNames.BucketCount; bucket++)
            {
                var partitionName = MessagePartitionNames.ForBucket(bucket);
                migrationBuilder.Sql($"""
                    CREATE TABLE {partitionName} PARTITION OF messages
                        FOR VALUES WITH (MODULUS {MessagePartitionNames.BucketCount}, REMAINDER {bucket});
                    """);
            }

            // The copy - see this migration's own remarks for why LEFT JOIN + COALESCE (not INNER JOIN)
            // is deliberate defence-in-depth, and why closing site_id's historical nullable gap here is
            // free rather than a separate concern.
            migrationBuilder.Sql("""
                INSERT INTO messages (
                    id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class,
                    site_id, attachment_id, client_message_id, content_kind, content, actions)
                SELECT
                    m.id, m.conversation_id, m.sequence, m.author_kind, m.author_id, m.body, m.created_at,
                    m.retention_class, COALESCE(m.site_id, c.site_id),
                    m.attachment_id, m.client_message_id, m.content_kind, m.content, m.actions
                FROM messages_pre_hash_partitioning m
                LEFT JOIN conversations c ON c.id = m.conversation_id;
                """);

            // Drops every one of the old table's own two partition levels along with it - Postgres
            // drops a partitioned table's whole subtree when the root is dropped (verified against a
            // real Postgres 17 while building 13-06, unchanged since).
            migrationBuilder.Sql("DROP TABLE messages_pre_hash_partitioning;");

            // Postgres requires every unique/PK constraint on a partitioned table to include the full
            // partition key - adr/0019's rule, applied to the new key (adr/0087's own Consequences
            // section: a narrower widening than before, since site_id alone replaces the two columns
            // created_at/retention_class used to require). Same original constraint names re-used
            // (freed by the DROP TABLE above), so nothing downstream sees a rename.
            migrationBuilder.Sql("ALTER TABLE messages ADD CONSTRAINT \"PK_messages\" PRIMARY KEY (id, site_id);");
            migrationBuilder.Sql("""
                ALTER TABLE messages ADD CONSTRAINT "FK_messages_conversations_conversation_id"
                    FOREIGN KEY (conversation_id) REFERENCES conversations (id) ON DELETE CASCADE;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_messages_conversation_id_sequence_site_id",
                table: "messages",
                columns: new[] { "conversation_id", "sequence", "site_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messages_conversation_id_client_message_id_site_id",
                table: "messages",
                columns: new[] { "conversation_id", "client_message_id", "site_id" },
                unique: true,
                filter: "client_message_id IS NOT NULL");

            // 14-06's own CHECK constraint - lost along with the rest of the old table's constraints
            // when it was renamed and dropped above, the same easy-to-miss re-add 13-06's own migration
            // already had to make once.
            migrationBuilder.Sql(
                "ALTER TABLE messages ADD CONSTRAINT ck_messages_content_length "
                + "CHECK (content IS NULL OR char_length(content) <= 16384);");

            // The two supporting indexes for 18-01's search - see this migration's own class-level
            // remarks for why these move here instead of staying a recurring background job. Built
            // once, per bucket - NOT against the partitioned parent: Postgres flatly refuses
            // `CREATE INDEX CONCURRENTLY` directly on a partitioned table ("cannot create index on
            // partitioned table ... concurrently", found by actually running this migration during
            // development, not assumed - the corrected understanding is recorded in this migration's
            // own class-level remarks). Building each of the 64 buckets' own local index CONCURRENTLY
            // is the documented workaround (PostgreSQL's own docs: "you may concurrently build the
            // index on each partition individually"), and each per-partition statement needs
            // suppressTransaction:true for the same reason any CONCURRENTLY statement does (it cannot
            // run inside the ambient transaction the rest of this Up() uses). No parent-level rolled-up
            // index is created to sit above them: nothing in this codebase ever addresses an index by
            // name at the `messages` parent level, and a partition's own local index is exactly as
            // usable by the planner for a query scoped to that partition whether or not it is attached
            // to a matching parent index definition.
            foreach (var bucketName in MessagePartitionNames.AllBucketNames)
            {
                migrationBuilder.Sql(
                    $"CREATE INDEX CONCURRENTLY ix_{bucketName}_site_created ON {bucketName} (site_id, created_at);",
                    suppressTransaction: true);
                migrationBuilder.Sql(
                    $"CREATE INDEX CONCURRENTLY ix_{bucketName}_search ON {bucketName} USING gin (to_tsvector('simple', body));",
                    suppressTransaction: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Stage2PartitionMessages's/Stage13RepartitionMessagesByRetentionClass's own reason, one
            // scheme deeper now: reassembling the previous two-level (retention_class, created_at)
            // grid from an arbitrary, already-deployed 64-bucket hash partitioning (after whatever the
            // DELETE-based retention sweep has since removed) is a data-recovery procedure, not a
            // migration rollback.
            throw new NotSupportedException(
                "Stage15RepartitionMessagesByTenantHash is one-way - reversing the hash repartitioning " +
                "would need to reassemble messages under the previous (retention_class, created_at) " +
                "scheme from an arbitrary already-deployed 64-bucket table (and whatever the DELETE-based " +
                "retention sweep has since removed), which is a data-recovery procedure, not a migration rollback.");
    }
}
