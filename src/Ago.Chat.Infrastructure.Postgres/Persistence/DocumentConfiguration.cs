using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `24-02`. <see cref="Document"/>'s own table, `documents` - the aggregate root that owns
/// <see cref="PublishedDocumentVersionConfiguration"/>'s own child rows, the identical
/// `Conversation`/`Message` shape <see cref="ConversationConfiguration"/> already establishes.
/// </summary>
internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id").HasConversion(IdConverters.Document).ValueGeneratedNever();
        builder.Property(d => d.DocumentKey).HasColumnName("document_key").HasMaxLength(Document.MaxDocumentKeyLength).IsRequired();
        builder.Property(d => d.LastSequence).HasColumnName("last_sequence");

        // One document per key - the same "the key IS the identity, a duplicate is a real conflict"
        // reasoning TagConfiguration's own unique (SiteId, Name) index gives, here unscoped since a
        // document key is global, not per-tenant.
        builder.HasIndex(d => d.DocumentKey).IsUnique().HasDatabaseName("ix_documents_key");

        // Postgres's own system column - the same optimistic-concurrency mechanism
        // ConversationConfiguration's own remarks describe in full, reused here so two concurrent
        // publishes for the same key cannot both compute the same next LastSequence.
        builder.Property<uint>("xmin").IsRowVersion();

        // Versions is a computed IReadOnlyList<PublishedDocumentVersion> reading the same _versions
        // field - without this Ignore, EF's own convention would claim _versions as that property's
        // backing field too, the identical collision ConversationConfiguration's own remarks describe
        // for Conversation.Messages.
        builder.Ignore(d => d.Versions);
        builder.Ignore(d => d.Current);

        // Never a settable collection (clean-architecture.md: no public setters) - EF is pointed at the
        // private backing field directly, so the aggregate loads without going through Publish.
        builder.HasMany<PublishedDocumentVersion>("_versions")
            .WithOne()
            .HasForeignKey(v => v.DocumentId)
            .OnDelete(DeleteBehavior.Restrict); // `24-02`: a version outlives its document row exactly as
                                                // long as the document row itself does - nothing in this codebase ever deletes a Document,
                                                // the same "no delete method exists" structural guarantee AcceptanceRecordConfiguration's
                                                // own remarks describe for erasure. Restrict rather than Cascade is what would make a
                                                // future DELETE on documents (one this codebase has no code path to issue) fail loudly
                                                // instead of silently taking every published version with it.
        builder.Navigation("_versions").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
