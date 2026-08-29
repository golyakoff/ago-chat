using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// `13-06`/`adr/0031`: `messages` becomes multi-level - `PARTITION BY LIST (retention_class)` at
    /// the top, each class itself `PARTITION BY RANGE (created_at)` monthly, exactly as `2-06` already
    /// partitioned it by month alone. Postgres cannot convert a partitioned table's own partition
    /// scheme in place any more than it could partition a plain table in place
    /// (`Stage2PartitionMessages`'s own remarks) - the only path is the identical rename/create/
    /// copy/drop technique that migration established, applied once more: rename the single-level
    /// table out of the way, build the two-level replacement (class-level partitions plus the current
    /// month and the next two under each, mirroring `PartitionMaintenanceJob`'s own bootstrap), copy
    /// every row across computing `retention_class` as part of the copy, drop the old table (which
    /// drops every one of its own monthly partitions with it - Postgres drops a partitioned table's
    /// whole subtree, verified against a real Postgres 17 while building this item), then re-add the
    /// primary key, foreign key and both unique indexes under their original names (renaming a table
    /// does not rename what belongs to it, so the old table's own constraints have to be gone first -
    /// `Stage2PartitionMessages`'s own comment on this point still applies verbatim).
    ///
    /// <para><b>`retention_class` for an existing row is a one-time approximation, stated as fact, not
    /// smoothed over.</b> The copy computes it from the owning site's <em>current</em>
    /// <see cref="Site.Tier"/> (joined `messages -&gt; conversations -&gt; sites`), because nothing in
    /// this schema has ever recorded what tier was active when a historical message was actually
    /// written - `RetentionClass`'s own remarks and `adr/0031`'s Scope section both name this
    /// explicitly. A message sent two years ago under a since-changed tier is stamped with today's
    /// tier's class here, once, at migration time; every message this system writes from the moment
    /// this migration completes onward gets its class from `Conversation.AddMessage`'s own real,
    /// write-time resolution instead (`RetentionClass.FromTier`, read through `3-04`'s site-config
    /// cache), and is never touched again either way. `COALESCE(s.tier, 'free')` covers the
    /// structurally-impossible case of a message whose conversation or site row is somehow already
    /// gone (both carry `ON DELETE CASCADE`, so this is defence-in-depth, not an expected path) by
    /// falling back to the safest, shortest-retention class rather than failing the whole migration
    /// over one anomalous row.</para>
    ///
    /// <para><b>`site_id` (`18-01`) and the three `14-06` structured-content columns travel across the
    /// copy unchanged, including `NULL`.</b> This migration is not `18-01`'s own backfill and must not
    /// pretend to be one: a row `MessageSiteIdBackfillJob` has not reached yet keeps a `NULL`
    /// `site_id` after this migration exactly as before it, and that job's next cycle continues
    /// converging on it - `MessagePartitionPruneQuery.ListPartitionsAsync` (the catalog read that job,
    /// `MessageSearchIndexJob` and `MessagePartitionPruneJob` all share) was updated in this same
    /// change to enumerate leaf partitions via `pg_partition_tree` instead of a direct `pg_inherits`
    /// lookup on `messages` - verified against a real Postgres 17 that the direct lookup would have
    /// returned only this migration's three new class-level partitions and none of the monthly leaves
    /// underneath them, silently stopping all three jobs from finding anything to do.</para>
    /// </summary>
    public partial class Stage13RepartitionMessagesByRetentionClass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE messages RENAME TO messages_pre_repartitioning;");

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
                    attachment_id uuid NULL,
                    client_message_id uuid NULL,
                    site_id uuid NULL,
                    content_kind character varying(64) NULL,
                    content text NULL,
                    actions text NULL
                ) PARTITION BY LIST (retention_class);
                """);

            // One class-level partition per RetentionClass.KnownClasses, each itself carrying the
            // current month plus the next two - the identical "3 months, computed from wall-clock
            // time" bootstrap Stage2PartitionMessages used for its own single level, now repeated
            // under each class (PartitionMaintenanceJob keeps this true per class per month from here
            // on, matching its own established "migration seeds it, the job maintains it" split).
            var monthStart = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
            foreach (var retentionClass in RetentionClass.KnownClasses)
            {
                var classPartition = MessagePartitionNames.ForClass(retentionClass);
                migrationBuilder.Sql($"""
                    CREATE TABLE {classPartition} PARTITION OF messages
                        FOR VALUES IN ('{retentionClass.Value}') PARTITION BY RANGE (created_at);
                    """);

                for (var i = 0; i < 3; i++)
                {
                    var from = monthStart.AddMonths(i);
                    var to = monthStart.AddMonths(i + 1);
                    var partitionName = MessagePartitionNames.ForMonth(retentionClass, from);
                    migrationBuilder.Sql($"""
                        CREATE TABLE {partitionName} PARTITION OF {classPartition}
                            FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
                        """);
                }
            }

            // The copy - see this migration's own class-level remarks for why COALESCE(s.tier, 'free')
            // and the LEFT JOINs are defence-in-depth rather than an expected path, and why this is a
            // one-time approximation for any row already written before today.
            migrationBuilder.Sql("""
                INSERT INTO messages (
                    id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class,
                    attachment_id, client_message_id, site_id, content_kind, content, actions)
                SELECT
                    m.id, m.conversation_id, m.sequence, m.author_kind, m.author_id, m.body, m.created_at,
                    COALESCE(s.tier, 'free'),
                    m.attachment_id, m.client_message_id, m.site_id, m.content_kind, m.content, m.actions
                FROM messages_pre_repartitioning m
                LEFT JOIN conversations c ON c.id = m.conversation_id
                LEFT JOIN sites s ON s.id = c.site_id;
                """);

            // Drops every one of the old table's own monthly partitions along with it - Postgres drops
            // a partitioned table's whole subtree when the root is dropped, no CASCADE needed (verified
            // against a real Postgres 17 while building this item, the same way Stage2PartitionMessages's
            // own single DROP TABLE relied on for its own (then-unpartitioned) old table).
            migrationBuilder.Sql("DROP TABLE messages_pre_repartitioning;");

            // Postgres requires every unique/PK constraint on a partitioned table to include the full
            // partition key - adr/0019's consequence, widened once more by adr/0031 to include
            // retention_class alongside created_at. Same original constraint names as
            // Stage2PartitionMessages re-used (freed by the DROP TABLE above), so nothing downstream -
            // this migration's own Designer/snapshot included - sees a rename.
            migrationBuilder.Sql("ALTER TABLE messages ADD CONSTRAINT \"PK_messages\" PRIMARY KEY (id, created_at, retention_class);");
            migrationBuilder.Sql("""
                ALTER TABLE messages ADD CONSTRAINT "FK_messages_conversations_conversation_id"
                    FOREIGN KEY (conversation_id) REFERENCES conversations (id) ON DELETE CASCADE;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_messages_conversation_id_sequence_created_at_retention_class",
                table: "messages",
                columns: new[] { "conversation_id", "sequence", "created_at", "retention_class" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messages_conversation_id_client_message_id_created_at_reten~",
                table: "messages",
                columns: new[] { "conversation_id", "client_message_id", "created_at", "retention_class" },
                unique: true,
                filter: "client_message_id IS NOT NULL");

            // `14-06`'s own CHECK constraint - lost along with the rest of the old table's constraints
            // when it was renamed and dropped above (the PK/FK/unique-index re-adds are not the only
            // thing that needs re-adding; this one is easy to miss because it is not part of the
            // partition-key story this migration is otherwise about). Found by
            // StructuredMessageContentPersistenceTests failing - a check whose own storage-level half
            // silently stopped being enforced would have been a real, quiet regression of `14-06`'s own
            // guarantee ("this page's own rule is anything enforcing a guarantee gets a constraint, not
            // just application code") had this migration shipped without it.
            migrationBuilder.Sql(
                "ALTER TABLE messages ADD CONSTRAINT ck_messages_content_length "
                + "CHECK (content IS NULL OR char_length(content) <= 16384);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Stage2PartitionMessages's own Down() gives the identical reason, one level deeper now:
            // reassembling a single-level-partitioned messages table from an arbitrary, already-deployed
            // two-level grid (however many class/month partitions PartitionMaintenanceJob has created
            // by the time anyone runs this Down, and after MessageArchiveJob/MessagePartitionPruneJob
            // may already have archived-and-dropped some of them) is a data-recovery procedure, not a
            // migration rollback - data-model.md's "explicitly marked one-way with a comment explaining
            // why" is satisfied by this comment, not by a Down() that would have to guess.
            throw new NotSupportedException(
                "Stage13RepartitionMessagesByRetentionClass is one-way - reversing the two-level " +
                "repartitioning would need to reassemble messages from every class/month partition " +
                "PartitionMaintenanceJob has since created (and MessagePartitionPruneJob may have already " +
                "archived and dropped some of), which is a data-recovery procedure, not a migration rollback.");
    }
}
