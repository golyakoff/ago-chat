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
        // nothing here needs composite-key change-tracking semantics. The *physical* primary key is
        // (id, site_id) as of `15-09`/`adr/0087`: Postgres requires every unique/PK constraint on a
        // partitioned table to include the partition column, and the partition key is now `site_id`
        // (`HASH`, 64 buckets) rather than `created_at`/`retention_class` - Stage15RepartitionMessagesByTenantHash
        // creates that composite PK by hand via raw SQL, the same "EF never validates a DbContext's
        // model against the live schema" reasoning `adr/0019` gave for the shape this one replaces.
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
        // does not defeat partition pruning. `15-09`/`adr/0087`: this is now also the physical
        // partition key (`PARTITION BY HASH (site_id)`, 64 buckets) - see this class's own `HasKey`
        // remarks. Non-nullable as of the same item (`Message.SiteId`'s own remarks explain why the
        // historical gap closed for good rather than staying a permanent nullable column). No
        // `HasIndex` here deliberately: the composite `(site_id, created_at)` index and the full-text
        // GIN index this column exists to serve are both built once, directly in
        // `Stage15RepartitionMessagesByTenantHash` via `CREATE INDEX CONCURRENTLY` - once per bucket,
        // not against the partitioned parent (Postgres refuses `CONCURRENTLY` directly on a partitioned
        // table; that migration's own remarks have the detail and the correction) - the same "raw SQL
        // owns this table's DDL, EF does not" split this table's partitioning has always followed,
        // except this time the fixed, one-time bucket count means the work fits in the migration
        // instead of needing a recurring background job (`MessageSearchIndexJob`'s own removal note has
        // the full reasoning).
        builder.Property(m => m.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);

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
        // to include the partition key in 2-06 (created_at) and again in 13-06 (retention_class) for
        // the reason adr/0019 gives - a real, documented weakening: two racing inserts for the same
        // (conversation_id, sequence) no longer collide here if they land in different partitions.
        // The primary defence against a genuine duplicate sequence was always the conversation
        // aggregate's optimistic-concurrency load-mutate-save (xmin), not this index - this stays the
        // last line of defence, not the first.
        //
        // `15-09`/`adr/0087`: the partition key changed shape, not the argument. `created_at` and
        // `retention_class` drop out (neither is part of the partition key any more) and `site_id`
        // takes their place - a *narrower* widening than before, per the ADR's own Consequences
        // section, and one with a real upside: uniqueness is now enforced within a tenant by the
        // database itself, not just approximately (a site's own messages all hash to the same bucket,
        // so this index once again catches every genuine same-conversation collision that index alone
        // could, the guarantee `adr/0019`'s own two-column widening had partially given up).
        builder.HasIndex(m => new { m.ConversationId, m.Sequence, m.SiteId }).IsUnique();

        // `5-07`: same adr/0019 shape (the partition key must be part of any unique constraint on
        // this table) applied to the retry-dedup column - the in-memory check in
        // `Conversation.AddMessage` is the mechanism actually relied on in the normal path (it also
        // catches a same-batch duplicate this index cannot, since both would still be un-committed
        // when it runs); this index is the storage-level backstop for two concurrent processes each
        // racing their own freshly-loaded copy of the aggregate. Filtered (partial) so the very
        // common `NULL` case - a caller that sent no clientMessageId at all - never collides with
        // itself.
        //
        // `15-09`/`adr/0087`: widened to `site_id` instead of `created_at`/`retention_class`, for the
        // identical reason the index above changed shape.
        builder.HasIndex(m => new { m.ConversationId, m.ClientMessageId, m.SiteId })
            .IsUnique()
            .HasFilter("client_message_id IS NOT NULL");
    }
}
