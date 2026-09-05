using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="AcceptanceRecord"/> - declared in Application, implemented in
/// `Ago.Chat.Infrastructure.Postgres` (clean-architecture.md's dependency rule: Application may not
/// reference `Npgsql`/EF Core directly, only the abstraction over it). Shaped by the two use cases
/// that need it (`RecordAcceptanceHandler`, `GetAcceptancesForSubjectHandler`), never a generic
/// <c>IRepository&lt;T&gt;</c> - the same reasoning <see cref="INoteRepository"/>'s own remarks give.
///
/// <para><b>No <c>DeleteAsync</c>, and that omission is this item's own erasure decision made
/// structural.</b> `24-01`'s Open question - does erasure remove an acceptance record - is answered
/// "no, it is kept whole as evidence of a lawful basis at the time" (`docs/adr/0111-*`). A port with no
/// delete method means there is no Application-layer call any handler could make to remove a row even
/// by mistake; the only way this table's rows could ever disappear is a change to the schema itself
/// or to `Ago.Chat.Worker`'s own raw-SQL erasure queries, both of which are exactly the "future change"
/// the erasure guard test (`AcceptanceRecordErasureGuardTests`) exists to catch.</para>
/// </summary>
public interface IAcceptanceRepository
{
    Task SaveAsync(AcceptanceRecord record, CancellationToken cancellationToken);

    /// <summary>Every acceptance a subject has ever recorded, oldest first - small and bounded (a
    /// person accepts a handful of documents, not thousands), the same "plain unbounded list" shape
    /// <see cref="INoteRepository.GetForConversationAsync"/> already uses for an identically-bounded
    /// read.</summary>
    Task<IReadOnlyList<AcceptanceRecord>> GetForSubjectAsync(
        AcceptanceSubjectKind subjectKind, Guid subjectId, CancellationToken cancellationToken);
}
