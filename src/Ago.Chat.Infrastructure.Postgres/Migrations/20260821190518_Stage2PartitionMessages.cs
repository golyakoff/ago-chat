using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// data-model.md's Partitioning section: converts <c>messages</c> to <c>PARTITION BY RANGE
    /// (created_at)</c>, monthly. Postgres cannot <c>ALTER TABLE</c> a regular table into a
    /// partitioned one - the only path is create-copy-drop, so this migration renames the old
    /// table out of the way, creates the partitioned replacement (plus the current month and the
    /// next two, so a fresh environment never starts with zero partitions - <see
    /// cref="PartitionMaintenanceJob"/> in <c>Ago.Chat.Worker</c> keeps that true going forward),
    /// copies every row across, and only then re-adds the primary key, foreign key and unique
    /// index - their names would collide with the old table's own constraints otherwise, since
    /// renaming a table does not rename what belongs to it.
    /// </summary>
    public partial class Stage2PartitionMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE messages RENAME TO messages_pre_partitioning;");

            migrationBuilder.Sql("""
                CREATE TABLE messages (
                    id uuid NOT NULL,
                    conversation_id uuid NOT NULL,
                    sequence integer NOT NULL,
                    author_kind text NOT NULL,
                    author_id uuid NOT NULL,
                    body text NOT NULL,
                    created_at timestamp with time zone NOT NULL
                ) PARTITION BY RANGE (created_at);
                """);

            // Current month and the next two - the same rule PartitionMaintenanceJob enforces daily
            // from here on (2-06's backlog item). Computed from wall-clock time because a migration
            // has no IClock to take it from and must reflect whenever it actually runs, on whatever
            // environment (a fresh CI Testcontainers Postgres included) - date-and-time.md still
            // applies (UTC), it just has no injectable clock at this layer.
            var monthStart = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 3; i++)
            {
                var from = monthStart.AddMonths(i);
                var to = monthStart.AddMonths(i + 1);
                var partitionName = $"messages_{from:yyyy_MM}";
                migrationBuilder.Sql($"""
                    CREATE TABLE {partitionName} PARTITION OF messages
                        FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
                    """);
            }

            migrationBuilder.Sql("""
                INSERT INTO messages (id, conversation_id, sequence, author_kind, author_id, body, created_at)
                SELECT id, conversation_id, sequence, author_kind, author_id, body, created_at
                FROM messages_pre_partitioning;
                """);

            // Drops the old table's PK/FK/unique index along with it, freeing their names - the new
            // table's own constraints (added next) reuse the same names deliberately, so nothing
            // downstream (this migration's own Designer/snapshot included) sees a rename.
            migrationBuilder.Sql("DROP TABLE messages_pre_partitioning;");

            // Postgres requires every unique/PK constraint on a partitioned table to include the
            // partition key column - MessageConfiguration's comment and adr/0019 explain the
            // consequence for the uniqueness guarantee this index provides.
            migrationBuilder.Sql("ALTER TABLE messages ADD CONSTRAINT \"PK_messages\" PRIMARY KEY (id, created_at);");
            migrationBuilder.Sql("""
                ALTER TABLE messages ADD CONSTRAINT "FK_messages_conversations_conversation_id"
                    FOREIGN KEY (conversation_id) REFERENCES conversations (id) ON DELETE CASCADE;
                """);
            migrationBuilder.CreateIndex(
                name: "IX_messages_conversation_id_sequence_created_at",
                table: "messages",
                columns: new[] { "conversation_id", "sequence", "created_at" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // data-model.md: "Always reversible, or explicitly marked one-way with a comment
            // explaining why." Reassembling a single non-partitioned table from an arbitrary,
            // already-deployed set of monthly partitions (however many PartitionMaintenanceJob has
            // created by the time anyone runs this Down) is not a migration - it is a data-recovery
            // procedure, and pretending otherwise would silently drop whichever partitions the
            // author of a future Down() forgot to enumerate.
            throw new NotSupportedException(
                "Stage2PartitionMessages is one-way - reversing table partitioning would need to " +
                "reassemble messages from every partition PartitionMaintenanceJob has since created, " +
                "which is a data-recovery procedure, not a migration rollback.");
    }
}
