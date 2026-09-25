using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`adr/0184` (O3). Resolved by <c>AddPersonNoteHandler</c>/<c>GetPersonNotesHandler</c> only -
/// see <see cref="IPersonNoteRepository"/>'s own remarks on why that narrowness is the point.</summary>
public sealed class PersonNoteRepository(AgoChatDbContext db) : IPersonNoteRepository
{
    public async Task SaveAsync(PersonNote note, CancellationToken cancellationToken)
    {
        // Notes are never edited once written (no mutating method on PersonNote), so every call is a
        // fresh insert - the identical reasoning NoteRepository.SaveAsync gives for conversation notes.
        db.PersonNotes.Add(note);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersonNote>> GetForPersonAsync(VisitorId personId, CancellationToken cancellationToken) =>
        await db.PersonNotes
            .Where(n => n.PersonId == personId)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
}
