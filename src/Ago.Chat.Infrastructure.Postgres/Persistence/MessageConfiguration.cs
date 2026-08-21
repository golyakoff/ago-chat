using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").HasConversion(IdConverters.Message).ValueGeneratedNever();
        builder.Property(m => m.ConversationId).HasColumnName("conversation_id").HasConversion(IdConverters.Conversation);
        builder.Property(m => m.Sequence).HasColumnName("sequence");
        builder.Property(m => m.AuthorKind).HasColumnName("author_kind").HasConversion<string>();
        builder.Property(m => m.AuthorId).HasColumnName("author_id");
        builder.Property(m => m.Body).HasColumnName("body").HasConversion(MessageBodyConverter.Instance);
        builder.Property(m => m.CreatedAt).HasColumnName("created_at");

        // data-model.md: turns duplicate delivery into a no-op insert at the storage level.
        builder.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();
    }
}
