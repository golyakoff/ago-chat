using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `24-02`. <see cref="PublishedDocumentVersion"/>'s own table, `published_document_versions` - a
/// child of <see cref="Document"/> (<see cref="DocumentConfiguration"/>'s own remarks), never written
/// to except by <see cref="Document.Publish"/> and never deleted (`Domain.PublishedDocumentVersion`'s
/// own "no delete method" remarks - the schema half of the same guarantee).
/// </summary>
internal sealed class PublishedDocumentVersionConfiguration : IEntityTypeConfiguration<PublishedDocumentVersion>
{
    public void Configure(EntityTypeBuilder<PublishedDocumentVersion> builder)
    {
        builder.ToTable("published_document_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id").HasConversion(IdConverters.PublishedDocumentVersion).ValueGeneratedNever();
        builder.Property(v => v.DocumentId).HasColumnName("document_id").HasConversion(IdConverters.Document).IsRequired();

        // `Domain.PublishedDocumentVersion`'s own remarks: denormalised deliberately, so the public
        // unauthenticated read path (IDocumentRepository.FindVersionAsync/FindCurrentAsync) never has
        // to join Document just to filter by the key it actually has.
        builder.Property(v => v.DocumentKey).HasColumnName("document_key")
            .HasMaxLength(PublishedDocumentVersion.MaxDocumentKeyLength).IsRequired();

        builder.Property(v => v.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(v => v.Version).HasColumnName("version")
            .HasMaxLength(PublishedDocumentVersion.MaxVersionLength).IsRequired();
        builder.Property(v => v.Title).HasColumnName("title").HasMaxLength(PublishedDocumentVersion.MaxTitleLength).IsRequired();
        builder.Property(v => v.Body).HasColumnName("body").HasMaxLength(PublishedDocumentVersion.MaxBodyLength).IsRequired();
        builder.Property(v => v.PublishedAt).HasColumnName("published_at").IsRequired();

        // The public read path's own two lookups (IDocumentRepository.FindVersionAsync/FindCurrentAsync)
        // both filter on document_key - FindVersionAsync adds an equality on version, FindCurrentAsync
        // orders by sequence descending and takes one row. One composite index the ordering-by-sequence
        // column ends serves both without a second one, and doubles as this table's own uniqueness
        // guard: two versions can never share a (document_key, sequence) pair, which is exactly the
        // "no two publishes for the same key ever collide on the number they were handed" invariant
        // Document.Publish's own increment-then-mint ordering is supposed to guarantee.
        builder.HasIndex(v => new { v.DocumentKey, v.Sequence }).IsUnique().HasDatabaseName("ix_published_document_versions_key_sequence");

        // FindVersionAsync's own predicate is (document_key, version), not (document_key, sequence) -
        // version is the string a caller (and an AcceptanceRecord) actually names, so it gets its own
        // index rather than making every specific-version lookup parse "v4" back into 4 first.
        builder.HasIndex(v => new { v.DocumentKey, v.Version }).IsUnique().HasDatabaseName("ix_published_document_versions_key_version");
    }
}
