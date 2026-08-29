using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");
        // EF's logical key stays id-only - MessageId (UUID v7) never collides in practice, and
        // nothing here needs composite-key change-tracking semantics. The *physical* primary key
        // is (id, created_at): Postgres requires every unique/PK constraint on a RANGE-partitioned
        // table to include the partition column, so Stage2PartitionMessages creates that composite
        // PK by hand via raw SQL (`data-model.md`'s Partitioning section) - this HasKey deliberately
        // does not mirror it, since EF never validates a DbContext's model against the live schema
        // and doing so would drag composite-key ceremony into every place a Message is tracked, for
        // no behavioural gain (2-06).
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").HasConversion(IdConverters.Message).ValueGeneratedNever();
        builder.Property(m => m.ConversationId).HasColumnName("conversation_id").HasConversion(IdConverters.Conversation);
        builder.Property(m => m.Sequence).HasColumnName("sequence");
        builder.Property(m => m.AuthorKind).HasColumnName("author_kind").HasConversion<string>();
        builder.Property(m => m.AuthorId).HasColumnName("author_id");
        builder.Property(m => m.Body).HasColumnName("body").HasConversion(MessageBodyConverter.Instance);
        // `5-03`: "message references the attachment, not the reverse" (`file-storage.md`). No FK
        // here either, for the same partitioning reason as attachments.message_id
        // (AttachmentConfiguration's own remarks) - this table is the partitioned side.
        builder.Property(m => m.AttachmentId).HasColumnName("attachment_id").HasConversion(IdConverters.NullableAttachment);
        // `5-07`: nullable - every caller before this shipped with no clientMessageId at all
        // (realtime.md's Client protocol section called it "a design intent, not wired up" since
        // `3-03`), and a NOT NULL column would reject their sends outright rather than simply
        // skipping dedup for them.
        builder.Property(m => m.ClientMessageId).HasColumnName("client_message_id");
        builder.Property(m => m.CreatedAt).HasColumnName("created_at");

        // `18-01`/`adr/0031` Addendum: denormalized straight onto `messages` rather than reached
        // through `conversations` by a join - the whole point being a tenant-scoped predicate that
        // does not defeat partition pruning. No `HasIndex` here deliberately: the composite
        // `(site_id, created_at)` index and the full-text GIN index this column exists to serve both
        // have to be built once per leaf partition with `CREATE INDEX CONCURRENTLY` (Postgres will not
        // let either run inside a transaction, and EF wraps a migration's `Up()` in one) - see
        // `MessageSearchIndexJob` in `Ago.Chat.Worker`, the same "raw SQL owns this table's DDL, EF
        // does not" split `PartitionMaintenanceJob`'s own remarks already establish for
        // `CREATE TABLE ... PARTITION OF`.
        builder.Property(m => m.SiteId).HasColumnName("site_id").HasConversion(IdConverters.NullableSite);

        // `13-06`/`adr/0031`: the immutable half of the two-level partition key. A plain `text`
        // conversion, not `IdConverters` (this is not an id) - the same "wrap a string, convert with a
        // one-line lambda pair" shape `MessageBodyConverter` establishes for a value type with no
        // identity of its own. `NOT NULL`, unlike `SiteId` above: `13-06`'s own migration backfills
        // every existing row as part of its rename/create/copy/drop (Postgres requires a value on
        // every row of a `LIST`-partitioned table's partition-key column), so there is no
        // "column added, old rows read null" window here the way there was for `SiteId`.
        builder.Property(m => m.RetentionClass).HasColumnName("retention_class")
            .HasConversion(rc => rc.Value, value => new RetentionClass(value));

        // `14-06`: the structured half, three nullable columns over Message's three private backing
        // fields - the same "computed property, EF pointed at the fields by name" shape
        // SiteConfiguration already uses for Site.WidgetConfig. The storage reasoning (text over
        // jsonb; three columns over one; why the actions column is the only one AGO Chat reads) is
        // in MessageContentConverters.
        builder.Property<MessageContentKind?>("_contentKind")
            .HasColumnName("content_kind")
            .HasMaxLength(MessageContentKind.MaxLength)
            .HasConversion(MessageContentConverters.Kind);
        builder.Property<MessagePayload?>("_payload")
            .HasColumnName("content")
            .HasConversion(MessageContentConverters.Payload);
        builder.Property<List<MessageAction>?>("_actions")
            .HasColumnName("actions")
            .HasConversion(MessageContentConverters.Actions, MessageContentConverters.ActionsComparer);
        builder.Ignore(m => m.Content);

        // data-model.md: turns duplicate delivery into a no-op insert at the storage level. Widened
        // to include created_at in 2-06 for the same partitioning reason as the PK above - a real,
        // documented weakening (adr/0019): two racing inserts for the same (conversation_id,
        // sequence) no longer collide here if their created_at values differ enough to land in
        // different partitions. The primary defence against a genuine duplicate sequence was always
        // the conversation aggregate's optimistic-concurrency load-mutate-save (xmin), not this
        // index - this stays the last line of defence, not the first.
        //
        // `13-06`/`adr/0031`: widened once more, to `retention_class` - the same consequence
        // `adr/0019` already argued was acceptable, applied a second time now that the partition key
        // itself has grown a second column. Two racing inserts for the same (conversation_id,
        // sequence) now also fail to collide here if they land in different *classes* as well as
        // different months - stated because it is a real further weakening of the same backstop, not
        // because it changes what actually prevents the race (Conversation's own xmin check, unchanged).
        builder.HasIndex(m => new { m.ConversationId, m.Sequence, m.CreatedAt, m.RetentionClass }).IsUnique();

        // `5-07`: same adr/0019 shape (partition key `created_at` must be part of any unique
        // constraint on this table) applied to the new retry-dedup column - the in-memory check in
        // `Conversation.AddMessage` is the mechanism actually relied on in the normal path (it also
        // catches a same-batch duplicate this index cannot, since both would still be un-committed
        // when it runs); this index is the storage-level backstop for two concurrent processes each
        // racing their own freshly-loaded copy of the aggregate, exactly the case adr/0019 already
        // named as this table's storage-level indexes' real job. Filtered (partial) so the very
        // common `NULL` case - a caller that sent no clientMessageId at all - never collides with
        // itself; Postgres treats every `NULL` in a unique index as distinct already, but the filter
        // also keeps the index smaller by not indexing rows it will never need to check.
        //
        // `13-06`: widened to `retention_class` for the identical reason the index above is.
        builder.HasIndex(m => new { m.ConversationId, m.ClientMessageId, m.CreatedAt, m.RetentionClass })
            .IsUnique()
            .HasFilter("client_message_id IS NOT NULL");
    }
}
