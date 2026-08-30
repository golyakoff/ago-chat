using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class ConversationTagRecordConfiguration : IEntityTypeConfiguration<ConversationTagRecord>
{
    public void Configure(EntityTypeBuilder<ConversationTagRecord> builder)
    {
        // `19-02`: the same "closed-vocabulary enum gets a CHECK constraint too" rule
        // ConversationConfiguration's own Outcome remarks state, applied here to TagSource.
        builder.ToTable("conversation_tags", t =>
        {
            t.HasCheckConstraint("ck_conversation_tags_source", "source IN ('Operator', 'Ai')");
        });
        builder.HasKey(x => new { x.ConversationId, x.TagId });
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id").HasConversion(IdConverters.Conversation);
        builder.Property(x => x.TagId).HasColumnName("tag_id").HasConversion(IdConverters.Tag);

        // `19-02`: non-nullable with a database default of 'Operator' - every row `18-04` ever wrote
        // before this column existed really was an operator's own action, so the default backfills
        // historical rows to the same true value new operator-applied rows get, the identical
        // "default backfills honestly, no separate backfill migration" shape
        // ConversationConfiguration.Outcome's own remarks describe for the same situation.
        builder.Property(x => x.Source).HasColumnName("source").HasConversion<string>()
            .IsRequired().HasDefaultValue(TagSource.Operator);

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
