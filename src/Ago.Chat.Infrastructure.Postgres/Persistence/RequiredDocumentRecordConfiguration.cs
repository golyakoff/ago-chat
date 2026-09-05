using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class RequiredDocumentRecordConfiguration : IEntityTypeConfiguration<RequiredDocumentRecord>
{
    public void Configure(EntityTypeBuilder<RequiredDocumentRecord> builder)
    {
        builder.ToTable("required_documents");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        // The same enum-as-text conversion AcceptanceRecordConfiguration already uses for the
        // identical AcceptanceSubjectKind column - a future fourth subject kind is additive, without a
        // migration to widen a `check` constraint.
        builder.Property(r => r.SubjectKind).HasColumnName("subject_kind").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.DocumentKey)
            .HasColumnName("document_key").HasMaxLength(Document.MaxDocumentKeyLength).IsRequired();

        // One row per (subject kind, document key) - a lawyer's later change is a row inserted or
        // deleted, never a duplicate of one already there. The only read this table serves
        // (IRequiredDocumentRepository.GetRequiredDocumentKeysAsync) filters on subject_kind alone, so
        // this same composite index also covers that query without a separate single-column one.
        builder.HasIndex(r => new { r.SubjectKind, r.DocumentKey }).IsUnique().HasDatabaseName("ix_required_documents_subject_kind_document_key");
    }
}
