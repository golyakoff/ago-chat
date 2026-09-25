using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `adr/0184` (O3): the write-and-read port for <see cref="PersonNote"/> - the operator's notes about a
/// person, held on the account's person registry. Resolved by <c>AddPersonNoteHandler</c> and
/// <c>GetPersonNotesHandler</c> only, the same narrow-by-design shape <see cref="INoteRepository"/>'s own
/// remarks describe for conversation notes: a note about a person is exactly the thing a visitor-facing
/// read must never be able to reach, and the fewer callers a port has, the easier that is to prove.
///
/// <para>No delete here, for the same reason <see cref="INoteRepository"/> has none: erasure is
/// <c>Ago.Chat.Worker</c>'s own raw-SQL job (<c>ConversationErasureQuery.DeletePersonNotesForVisitorAsync</c>),
/// never an Application-layer port.</para>
/// </summary>
public interface IPersonNoteRepository
{
    Task SaveAsync(PersonNote note, CancellationToken cancellationToken);

    /// <summary>Every note about one person, oldest first - small and bounded, the same unbounded-list
    /// justification <see cref="INoteRepository.GetForConversationAsync"/> gives for itself.</summary>
    Task<IReadOnlyList<PersonNote>> GetForPersonAsync(VisitorId personId, CancellationToken cancellationToken);
}
