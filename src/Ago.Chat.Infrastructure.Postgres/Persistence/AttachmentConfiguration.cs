using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").HasConversion(IdConverters.Attachment).ValueGeneratedNever();
        builder.Property(a => a.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(a => a.ConversationId).HasColumnName("conversation_id").HasConversion(IdConverters.Conversation);
        // No foreign key to messages(id): `messages` is range-partitioned by created_at (`2-06`), so
        // Postgres requires any unique constraint it references to include the partition column -
        // messages' own primary key is (id, created_at), not id alone, so a plain FK on this column
        // is not possible. Documented as a friction (data-model.md), not hidden.
        builder.Property(a => a.MessageId).HasColumnName("message_id").HasConversion(IdConverters.NullableMessage);
        builder.Property(a => a.ObjectKey).HasColumnName("object_key").IsRequired();
        builder.Property(a => a.ContentType).HasColumnName("content_type").IsRequired();
        builder.Property(a => a.SizeBytes).HasColumnName("size_bytes");
        builder.Property(a => a.State).HasColumnName("state").HasConversion<string>();
        builder.Property(a => a.ThumbnailKey).HasColumnName("thumbnail_key");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");

        // `23-76`: populated only by `Ago.Chat.Worker`'s own `AttachmentDeduplicationConsumer`, after
        // confirm - see `Attachment.SetContentHash`/`Attachment.PointToExistingObject`'s own remarks
        // for why this cannot be known at confirm time itself, still less at presign. Nullable and
        // unpopulated for every attachment that predates this column, the same "the column exists
        // before its writer does" shape `ThumbnailKey` above already established for `5-04`.
        builder.Property(a => a.ContentHash).HasColumnName("content_hash");

        builder.HasOne<Site>().WithMany().HasForeignKey(a => a.SiteId);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(a => a.ConversationId);

        // `23-76`: the dedup lookup's own index - "the same bytes uploaded repeatedly cost one object,
        // within a tenant" (this item's own Done-when) needs exactly this predicate: does a *Ready*
        // attachment for this *site* already carry this hash. Filtered on both `state = 'Ready'` and
        // `content_hash IS NOT NULL` - a `Pending` row (hash not yet computed) or a pre-`23-76` row
        // (hash never computed) must never be returned as a match, and the partial index costs nothing
        // for either since neither predicate holds for those rows. Deliberately not `IsUnique` - two
        // attachments legitimately share one hash once dedup has already repointed the second at the
        // first's own object key (`Attachment.PointToExistingObject`), and a third upload of the same
        // content dedups onto whichever the lookup finds - within-tenant only, `adr/0108`'s own
        // rewrite-not-delete reasoning is exactly why nothing here is ever scoped across tenants.
        builder.HasIndex(a => new { a.SiteId, a.ContentHash })
            .HasDatabaseName("ix_attachments_site_content_hash")
            .HasFilter("state = 'Ready' AND content_hash IS NOT NULL");

        // No index on (state, created_at) yet - `5-03` has no query that filters attachments by
        // state (GetByIdAsync is a PK lookup). `5-04`'s orphan sweep gets one when it gets a real
        // reader (db-migration skill: "every new query path gets its index decided consciously"),
        // not speculatively ahead of one.
    }
}
