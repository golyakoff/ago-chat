using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `14-14`: its own table, deliberately never `channel_identities` - see
/// <see cref="VisitorContactDetail"/>'s own remarks for why. No `site_id` column - the same
/// `ConversationNoteConfiguration` precedent: tenant scope is checked one level up, through the
/// conversation the operator is looking at (`RecordVisitorContactDetailHandler`/
/// `DeleteVisitorContactDetailHandler` both resolve it via `IConversationRepository` first), so a
/// column with no query that ever filters on it would be exactly the premature column
/// `data-model.md`'s "an index arrives with its first real reader" discipline warns against, one level
/// up from indexes. <b>No unique index either</b> - the backlog item's own explicit instruction, since
/// a visitor may plausibly hold more than one contact detail of the same kind (a personal phone and a
/// work phone).
/// </summary>
internal sealed class VisitorContactDetailConfiguration : IEntityTypeConfiguration<VisitorContactDetail>
{
    public void Configure(EntityTypeBuilder<VisitorContactDetail> builder)
    {
        builder.ToTable("visitor_contact_details");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id)
            .HasColumnName("id").HasConversion(IdConverters.VisitorContactDetail).ValueGeneratedNever();
        builder.Property(d => d.VisitorId).HasColumnName("visitor_id").HasConversion(IdConverters.Visitor);

        // Stored as the CLR member name - ChannelIdentityConfiguration's own precedent for `Kind`,
        // for the identical reason: nothing constrains these values with a CHECK constraint, so the
        // plain default HasConversion<string>() is honest.
        builder.Property(d => d.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);

        builder.Property(d => d.Value)
            .HasColumnName("value")
            .HasMaxLength(VisitorContactDetail.MaxValueLength)
            .IsRequired();

        builder.Property(d => d.RecordedByOperatorId).HasColumnName("recorded_by_operator_id").HasConversion(IdConverters.Operator);
        builder.Property(d => d.RecordedAt).HasColumnName("recorded_at");

        // The only real read (GetForVisitorAsync) filters on visitor_id alone, ordered by
        // recorded_at - one composite index serves both without a separate sort, the identical shape
        // ix_conversation_notes_conversation already uses for ConversationNote's own single read.
        builder.HasIndex(d => new { d.VisitorId, d.RecordedAt }).HasDatabaseName("ix_visitor_contact_details_visitor");

        // Cascade: a visitor's own erasure is expected to drain this table the same way
        // ConversationErasureQuery already drains conversation_notes explicitly for a closed
        // conversation - this FK is defence in depth for a stray row that sweep somehow missed, the
        // same "primary mechanism is explicit, cascade is the backstop" shape
        // ConversationNoteConfiguration's own remarks describe for its own foreign key.
        builder.HasOne<Visitor>().WithMany().HasForeignKey(d => d.VisitorId).OnDelete(DeleteBehavior.Cascade);
    }
}
