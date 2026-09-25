using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`adr/0184` (O3): <c>person_notes</c> - operator notes about a person, on the account's person
/// registry. Same shape as <c>conversation_notes</c> (<see cref="ConversationNoteConfiguration"/>) under a
/// different, longer-lived parent: the person, not one conversation.</summary>
internal sealed class PersonNoteConfiguration : IEntityTypeConfiguration<PersonNote>
{
    public void Configure(EntityTypeBuilder<PersonNote> builder)
    {
        builder.ToTable("person_notes");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasColumnName("id").HasConversion(IdConverters.PersonNote).ValueGeneratedNever();
        builder.Property(n => n.PersonId).HasColumnName("visitor_id").HasConversion(IdConverters.Visitor);
        builder.Property(n => n.AuthorId).HasColumnName("author_id").HasConversion(IdConverters.Operator);
        builder.Property(n => n.Body).HasColumnName("body").HasMaxLength(PersonNote.MaxBodyLength).IsRequired();
        builder.Property(n => n.CreatedAt).HasColumnName("created_at");

        // Cascade from the person: a person's own erasure explicitly drains this table first
        // (ConversationErasureQuery.DeletePersonNotesForVisitorAsync, keyed to the visitor exactly the way
        // the contact-details drain is) - this FK is defence in depth for a stray row that sequence somehow
        // missed, the same "primary mechanism is explicit, cascade is the backstop" shape
        // ConversationNoteConfiguration documents for its own parent.
        builder.HasOne<Visitor>().WithMany().HasForeignKey(n => n.PersonId).OnDelete(DeleteBehavior.Cascade);

        // The only real read (GetForPersonAsync) filters on visitor_id alone, ordered by created_at - one
        // composite index serves both without a separate sort.
        builder.HasIndex(n => new { n.PersonId, n.CreatedAt }).HasDatabaseName("ix_person_notes_person");
    }
}
