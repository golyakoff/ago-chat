using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`14-09`: <see cref="EmailThreadState"/>'s own table - keyed directly by
/// <see cref="ConversationId"/> (a 1:1 extension of <c>conversations</c>, not a synthetic id -
/// <see cref="EmailThreadState"/>'s own remarks explain why).</summary>
internal sealed class EmailThreadStateConfiguration : IEntityTypeConfiguration<EmailThreadState>
{
    public void Configure(EntityTypeBuilder<EmailThreadState> builder)
    {
        builder.ToTable("email_threads");
        builder.HasKey(t => t.ConversationId);
        builder.Property(t => t.ConversationId)
            .HasColumnName("conversation_id").HasConversion(IdConverters.Conversation).ValueGeneratedNever();

        builder.Property(t => t.RootMessageId).HasColumnName("root_message_id").IsRequired().HasMaxLength(ExternalMessageId.MaxLength);
        builder.Property(t => t.LastInboundMessageId)
            .HasColumnName("last_inbound_message_id").IsRequired().HasMaxLength(ExternalMessageId.MaxLength);

        // No product requirement pins an email subject's own maximum length; RFC 2822 does not bound it
        // either. Bounded to the same generous ceiling MessageBody uses, for the identical "small enough
        // that one row is never the reason an insert is slow" reasoning - a subject line living inside a
        // header is orders of magnitude smaller than this in every real message.
        builder.Property(t => t.Subject).HasColumnName("subject").IsRequired().HasMaxLength(MessageBody.MaxLength);

        builder.HasOne<Conversation>().WithMany().HasForeignKey(t => t.ConversationId);
    }
}
