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

        // `23-82`/`23-80`: see Attachment.DownloadCount's own remarks. A plain default-0 counter and
        // a nullable timestamp, the same shape `attachment_bytes_reserved` (sites) already uses for a
        // maintained running total.
        builder.Property(a => a.DownloadCount).HasColumnName("download_count").HasDefaultValue(0L);
        builder.Property(a => a.LastDownloadedAt).HasColumnName("last_downloaded_at");

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

        // `23-80`: the storage screen's own two real readers - `ListSiteAttachmentsHandler` filters
        // every query to this tenant's `Ready` rows and defaults to "by size, descending" (the
        // backlog item's own words: "somebody clearing space wants the top of that list"), so that is
        // the index built for the default path, not a speculative one ahead of a real reader (the
        // `5-04` comment this replaces named exactly that discipline - this is the reader arriving).
        // The type/conversation/sender sort orders reuse the base `(site_id, state)` prefix below and
        // sort the already-narrowed row set without their own index - a tenant's own attachment count
        // is not the platform-wide row count `5-04`'s orphan sweep was reasoning about, and a second
        // and third single-purpose index for every remaining sort order was not judged worth its own
        // write cost without a measured case that the base-filtered sort is actually slow (CLAUDE.md
        // rule 7).
        builder.HasIndex(a => new { a.SiteId, a.State, a.SizeBytes })
            .HasDatabaseName("ix_attachments_site_state_size")
            .HasFilter("state = 'Ready'");

        // The age sort - "what most cleanups are actually keyed on" (23-80's own words) - and the
        // never-downloaded filter both key off a second, equally cheap prefix; combined into one
        // index rather than two because `created_at` and `download_count` are both narrow columns a
        // single btree carries without meaningfully growing over the size-sorted index above.
        builder.HasIndex(a => new { a.SiteId, a.State, a.CreatedAt, a.DownloadCount })
            .HasDatabaseName("ix_attachments_site_state_created_download")
            .HasFilter("state = 'Ready'");
    }
}
