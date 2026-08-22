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

        builder.HasOne<Site>().WithMany().HasForeignKey(a => a.SiteId);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(a => a.ConversationId);

        // No index on (state, created_at) yet - `5-03` has no query that filters attachments by
        // state (GetByIdAsync is a PK lookup). `5-04`'s orphan sweep gets one when it gets a real
        // reader (db-migration skill: "every new query path gets its index decided consciously"),
        // not speculatively ahead of one.
    }
}
