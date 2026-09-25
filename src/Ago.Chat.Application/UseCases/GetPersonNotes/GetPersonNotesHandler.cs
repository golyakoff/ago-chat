using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetPersonNotes;

/// <summary>`adr/0184` (O3): every note about one person, oldest first. Gated by
/// <see cref="Permission.ConversationRead"/> - a note is read context for whoever can already read the
/// person's conversations, the same reasoning <c>GetConversationNotesHandler</c> gives for itself.</summary>
public sealed class GetPersonNotesHandler(
    IVisitorRepository visitors, IPersonNoteRepository notes, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<PersonNoteDto>>> HandleAsync(GetPersonNotes query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var person = await visitors.GetByIdAsync(query.PersonId, cancellationToken);
        if (person is null || person.SiteId != query.SiteId)
        {
            return PersonErrors.NotFound(query.PersonId.Value);
        }

        var items = await notes.GetForPersonAsync(person.Id, cancellationToken);

        return Result<IReadOnlyList<PersonNoteDto>>.Success(
            items.Select(n => new PersonNoteDto(n.Id.Value, n.AuthorId.Value, n.Body, n.CreatedAt)).ToList());
    }
}
