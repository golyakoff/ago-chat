using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `24-01`. Its own table, `acceptance_records` - never folded into `operators`/`visitors`/`sites`,
/// the same "its own aggregate, its own table" reasoning <see cref="ConversationNote"/>'s own remarks
/// give, here for an even stronger reason: a subject can accept many documents over time, and each
/// acceptance is itself immutable evidence, not a mutable property of the subject row.
///
/// <para><b>No foreign key on <c>subject_id</c> - deliberately, and this is the erasure decision made
/// schema.</b> `docs/adr/0111-*` decides that erasure does not remove an acceptance record: it is kept
/// whole as evidence that processing had a lawful basis at the time. A required FK to
/// `sites`/`operators`/`visitors` would cascade this table into every one of those aggregates' own
/// deletion paths (`SiteErasureQuery.DeleteSiteAsync`'s cascade list, `ConversationErasureQuery`'s
/// per-visitor drain) exactly the way `conversation_notes`/`visitor_contact_details` are deliberately
/// wired *to* cascade - the opposite of what this table needs. This is the same shape
/// `conversation_assignments` already established for the identical reason in a different direction
/// (`ConversationAssignmentIntervalConfiguration`'s own remarks: "No foreign key on `conversation_id`,
/// deliberately... kept precisely so a tenant's workload numbers survive a visitor's own erasure
/// request") - here the thing being kept is evidence rather than a workload count, but the mechanism
/// is identical: no FK means no cascade means nothing to accidentally wire up later.
/// <see cref="AcceptanceRecordErasureGuardTests"/> (`Ago.Chat.Integration.Tests`) is the guard that
/// keeps a future "for consistency" FK from quietly reversing this.</para>
/// </summary>
internal sealed class AcceptanceRecordConfiguration : IEntityTypeConfiguration<AcceptanceRecord>
{
    public void Configure(EntityTypeBuilder<AcceptanceRecord> builder)
    {
        builder.ToTable("acceptance_records");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").HasConversion(IdConverters.AcceptanceRecord).ValueGeneratedNever();

        builder.Property(a => a.SubjectKind).HasColumnName("subject_kind").HasConversion<string>().HasMaxLength(20).IsRequired();

        // A bare Guid, not a strongly-typed id converter - AcceptanceSubjectKind's own remarks explain
        // why one column widens across three id types instead of three nullable columns.
        builder.Property(a => a.SubjectId).HasColumnName("subject_id").IsRequired();

        builder.Property(a => a.DocumentKey).HasColumnName("document_key").HasMaxLength(AcceptanceRecord.MaxDocumentKeyLength).IsRequired();
        builder.Property(a => a.DocumentVersion)
            .HasColumnName("document_version").HasMaxLength(AcceptanceRecord.MaxDocumentVersionLength).IsRequired();
        builder.Property(a => a.AcceptedAt).HasColumnName("accepted_at").IsRequired();
        builder.Property(a => a.ClientIp).HasColumnName("client_ip").HasMaxLength(AcceptanceRecord.MaxClientIpLength);
        builder.Property(a => a.UserAgent).HasColumnName("user_agent").HasMaxLength(AcceptanceRecord.MaxUserAgentLength);

        // The only real read (GetForSubjectAsync) filters on (subject_kind, subject_id), ordered by
        // accepted_at - one composite index serves both without a separate sort, the same shape
        // ConversationNoteConfiguration's own index gives for an identical access pattern.
        builder.HasIndex(a => new { a.SubjectKind, a.SubjectId, a.AcceptedAt }).HasDatabaseName("ix_acceptance_records_subject");
    }
}
