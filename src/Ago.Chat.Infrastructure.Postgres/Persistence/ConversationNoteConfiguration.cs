using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `18-04`: its own table, deliberately never `messages` - see <see cref="ConversationNote"/>'s own
/// remarks in full for why. No `site_id` column: unlike `messages` (`18-01`'s own denormalization,
/// needed for a tenant-scoped full-text search that can prune partitions), a note is only ever reached
/// by conversation id, already tenant-checked one level up
/// (<c>AddConversationNoteHandler</c>/<c>GetConversationNotesHandler</c> both resolve the conversation
/// through <c>IConversationReadStore.GetByIdAsync(id, siteId, ...)</c> first) - adding a column with no
/// query that filters on it would be exactly the premature column `data-model.md`'s "an index arrives
/// with its first real reader" discipline warns against, one level up from indexes.
/// </summary>
internal sealed class ConversationNoteConfiguration : IEntityTypeConfiguration<ConversationNote>
{
    public void Configure(EntityTypeBuilder<ConversationNote> builder)
    {
        builder.ToTable("conversation_notes");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasColumnName("id").HasConversion(IdConverters.ConversationNote).ValueGeneratedNever();
        builder.Property(n => n.ConversationId).HasColumnName("conversation_id").HasConversion(IdConverters.Conversation);
        builder.Property(n => n.AuthorId).HasColumnName("author_id").HasConversion(IdConverters.Operator);
        builder.Property(n => n.Body).HasColumnName("body").HasMaxLength(ConversationNote.MaxBodyLength).IsRequired();
        builder.Property(n => n.CreatedAt).HasColumnName("created_at");

        // Cascade: a conversation's own erasure explicitly drains this table first
        // (ConversationErasureQuery.DeleteNotesForConversationAsync) - this FK is defence in depth for
        // a stray row that sequence somehow missed, the same "primary mechanism is explicit, cascade
        // is the backstop" shape ConversationConfiguration's own `_messages` navigation documents.
        builder.HasOne<Conversation>().WithMany().HasForeignKey(n => n.ConversationId).OnDelete(DeleteBehavior.Cascade);

        // The only real read (GetForConversationAsync) filters on conversation_id alone, ordered by
        // created_at - one composite index serves both without a separate sort.
        builder.HasIndex(n => new { n.ConversationId, n.CreatedAt }).HasDatabaseName("ix_conversation_notes_conversation");
    }
}
