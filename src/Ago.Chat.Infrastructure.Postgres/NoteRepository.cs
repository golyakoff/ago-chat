using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `18-04`. Resolved by <c>AddConversationNoteHandler</c>/<c>GetConversationNotesHandler</c> only -
/// no other handler in this codebase depends on <see cref="INoteRepository"/>, which is the concrete
/// expression of <see cref="ConversationNote"/>'s own "structurally incapable" remarks: this class,
/// like the interface it implements, shares no base type, no method, and no SQL with
/// <see cref="ConversationRepository"/>/<see cref="ConversationReadStore"/>.
/// </summary>
public sealed class NoteRepository(AgoChatDbContext db) : INoteRepository
{
    public async Task SaveAsync(ConversationNote note, CancellationToken cancellationToken)
    {
        // Notes are never edited once written (no Rename-shaped method on ConversationNote), so the
        // detached-vs-tracked branch WebhookEndpointRepository.SaveAsync needs does not apply here -
        // every call is a fresh insert.
        db.ConversationNotes.Add(note);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationNote>> GetForConversationAsync(
        ConversationId conversationId, CancellationToken cancellationToken) =>
        await db.ConversationNotes
            .Where(n => n.ConversationId == conversationId)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
}
