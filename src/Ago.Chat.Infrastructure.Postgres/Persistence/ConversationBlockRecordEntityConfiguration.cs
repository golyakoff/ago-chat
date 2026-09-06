using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `24-10`: `conversation_block_records` - one row per act (a block, or its later reversal), never per
/// state. See <see cref="ConversationBlockRecordEntity"/>'s own remarks for why this table, unlike
/// `erasure_records`/`access_records`, does carry a real foreign key back to `conversations` and is
/// allowed to cascade away with it.
/// </summary>
internal sealed class ConversationBlockRecordEntityConfiguration : IEntityTypeConfiguration<ConversationBlockRecordEntity>
{
    public void Configure(EntityTypeBuilder<ConversationBlockRecordEntity> builder)
    {
        builder.ToTable("conversation_block_records");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.ConversationId).HasColumnName("conversation_id").HasConversion(IdConverters.Conversation).IsRequired();
        builder.Property(e => e.SiteId).HasColumnName("site_id").IsRequired();
        builder.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        builder.Property(e => e.ActorId).HasColumnName("actor_id").IsRequired();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();

        // `ck_conversation_block_records_kind`: the same "a CHECK constraint backstops the enum at the
        // storage level" reasoning ErasureRecordEntityConfiguration's own remarks give for
        // erasure_records.
        builder.ToTable(t => t.HasCheckConstraint("ck_conversation_block_records_kind", "kind IN ('Blocked', 'Unblocked')"));

        // Cascade: this table's own purpose ends when the conversation it is about is gone
        // (ConversationBlockRecordEntity's own remarks on why this differs from erasure_records/
        // access_records) - no explicit pre-delete query anywhere drains this table the way
        // ConversationErasureQuery does for conversation_notes/conversation_tags, so the FK is the
        // only mechanism, not a backstop for one.
        builder.HasOne<Conversation>().WithMany()
            .HasForeignKey(e => e.ConversationId).OnDelete(DeleteBehavior.Cascade);

        // Serves a conversation's own audit trail read (by id, newest first) - the one query this table
        // exists to answer besides the insert, the same "index the columns the WHERE/ORDER BY actually
        // use together" reasoning every other keyset-shaped index in this codebase follows.
        builder.HasIndex(e => new { e.ConversationId, e.OccurredAt })
            .HasDatabaseName("ix_conversation_block_records_conversation");
    }
}
