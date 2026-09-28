using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetPersonConversations;

/// <summary>
/// `26-269`: the client-detail hub's "which dialog do I open for this person" read - the design doc's
/// §5 NEW chat read (`docs/backlog/26-269-clients-redesign.md`). The console/android hold a `personId`
/// from the calendar's `Contact` list, not a `ConversationId`, so `GetVisitorHistoryHandler` (which
/// starts from a conversation the operator is already inside) cannot serve this call - this handler
/// starts from the person alone.
///
/// <para><b>Gated on the same <see cref="Permission.ConversationRead"/> the sibling person reads
/// use</b> (<see cref="GetPersons.GetPersonsHandler"/>, <see cref="GetPersonNotes.GetPersonNotesHandler"/>),
/// per the design doc §6: "no new gate if it reuses <c>conversation:read</c>... with the site scope the
/// existing conversation reads use." A person's own conversations are exactly the facts that permission
/// already lets an operator read one conversation at a time; reading them by person id is the same
/// capability, not a wider one - no new permission was needed, so none was added.</b></para>
///
/// <para><b>Tenant isolation the same shape <see cref="GetPersonNotes.GetPersonNotesHandler"/> already
/// uses</b> - a person id that does not exist, or resolves to another account's visitor, is
/// <see cref="PersonErrors.NotFound"/>, not a narrower code that would confirm the id exists elsewhere.</para>
///
/// <para><b>Degrades to an empty list, never an error, when the person has no conversations</b>
/// (`adr/0184` decision 4's own "no dialog to open yet" - the design doc §5 states this explicitly for
/// this exact read). A manual client created by the `26-268` booking flow has none; the client-detail
/// hub hides its "Открыть диалог" action rather than greying it, the same "hide, don't grey" rule the
/// confirmed-bookings screen already applies to a null `originConversationId` (design doc §4).</para>
/// </summary>
public sealed class GetPersonConversationsHandler(
    IVisitorRepository visitors, IConversationReadStore readStore, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<PersonConversationDto>>> HandleAsync(
        GetPersonConversations query, CancellationToken cancellationToken)
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

        var items = await readStore.GetConversationsForPersonAsync(person.Id, cancellationToken);

        return Result<IReadOnlyList<PersonConversationDto>>.Success(items.Select(ToDto).ToList());
    }

    private static PersonConversationDto ToDto(PersonConversationItem item) => new(
        item.Id.Value,
        item.State,
        IsActive: item.State != nameof(ConversationState.Closed),
        item.StartedAt,
        item.ClosedAt,
        item.LastActivityAt);
}
