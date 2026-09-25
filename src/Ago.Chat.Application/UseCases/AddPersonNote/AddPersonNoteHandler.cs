using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AddPersonNote;

/// <summary>
/// `adr/0184` (author decision O3): an operator writes a note about a person on the account's person
/// registry - the note the calendar's deleted <c>customers.notes</c> used to hold.
///
/// <para>Gated by <see cref="Permission.ConversationNoteWrite"/>, the same permission a conversation note
/// needs - a note about a person is the same act (an operator recording private context about a visitor)
/// under a longer-lived key, not a new capability. The person must belong to the caller's own account;
/// another account's person id reads as <see cref="PersonErrors.NotFound"/>, never as forbidden.</para>
/// </summary>
public sealed class AddPersonNoteHandler(
    IVisitorRepository visitors,
    IPersonNoteRepository notes,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<AddedPersonNote>> HandleAsync(AddPersonNote command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationNoteWrite, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to write notes on this site's conversations.");
        }

        var person = await visitors.GetByIdAsync(command.PersonId, cancellationToken);
        if (person is null || person.SiteId != command.SiteId)
        {
            return PersonErrors.NotFound(command.PersonId.Value);
        }

        var now = clock.UtcNow;
        PersonNote note;
        try
        {
            note = PersonNote.Write(new PersonNoteId(idGenerator.NewId(now)), person.Id, command.RequestedBy, command.Body, now);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.NoteInvalid(ex.Message);
        }

        await notes.SaveAsync(note, cancellationToken);

        return new AddedPersonNote(note.Id.Value, note.PersonId.Value, note.AuthorId.Value, note.Body, note.CreatedAt);
    }
}
