using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class ConversationTagRecordConfiguration : IEntityTypeConfiguration<ConversationTagRecord>
{
    public void Configure(EntityTypeBuilder<ConversationTagRecord> builder)
    {
        builder.ToTable("conversation_tags");
        builder.HasKey(x => new { x.ConversationId, x.TagId });
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id").HasConversion(IdConverters.Conversation);
        builder.Property(x => x.TagId).HasColumnName("tag_id").HasConversion(IdConverters.Tag);

        // Both cascade: `16-02`'s conversation erasure explicitly drains this table first
        // (defence in depth here too, same shape as ConversationNoteConfiguration); DeleteTagHandler
        // relies on the tag-side cascade as its *primary* mechanism (ITagRepository.DeleteAsync's own
        // remarks - the table this row lives in has no other reason to keep a row once its tag is
        // gone).
        builder.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);

        // Reverse-direction lookup (`GetAllForSiteAsync`'s exists() filter, GetConversationIdsForTagAsync)
        // needs tag_id first - the primary key above already covers conversation_id-first lookups
        // (GetForConversationAsync).
        builder.HasIndex(x => x.TagId).HasDatabaseName("ix_conversation_tags_tag_id");
    }
}
