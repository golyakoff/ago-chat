using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-03`: the write-side port for <see cref="ConversationAssignmentInterval"/> - narrow, and shaped
/// by its three real Application-layer callers (<c>AssignConversationHandler</c>,
/// <c>TransferConversationHandler</c>, <c>CloseConversationHandler</c>), never a generic
/// <c>IRepository&lt;T&gt;</c> (`clean-architecture.md`), matching <c>INoteRepository</c>'s own
/// precedent for a small standalone entity with no aggregate of its own.
///
/// <para><b>Why this exists at all, rather than the three handlers touching <c>AgoChatDbContext</c>
/// directly.</b> CLAUDE.md rule 2: no <c>DbContext</c>, no Npgsql, above Infrastructure - a handler
/// must not know what a <see cref="ConversationAssignmentInterval"/> is persisted with. The dependency
/// rule is also why the interface lives in <c>Ago.Chat.Application</c> rather than nearer its
/// implementation: Application may depend on nothing outside itself and Domain, so a port Application
/// calls has to be declared where Application can see it.</para>
///
/// <para><b>Neither method commits.</b> Both only stage a change against whatever unit of work the
/// caller's own <c>AgoChatDbContext</c> is already part of - an <see cref="Open"/>'d interval is
/// <c>Add</c>-ed to the change tracker, a <see cref="CloseOpenAsync"/> mutates an already-tracked row's
/// <c>EndedAt</c> - so the interval and the conversation's own state change land in the exact same
/// <c>SaveChangesAsync</c> (or, for <c>TransferConversationHandler</c>, the same explicit
/// <c>IUnitOfWork</c> transaction) the caller was going to run anyway (CLAUDE.md rule 4). Giving this
/// port its own <c>SaveChangesAsync</c> - the shape <c>INoteRepository.SaveAsync</c> uses, correctly,
/// for an interval-free write with nothing else to be atomic with - would instead commit the interval
/// on its own, ahead of the conversation's own save, and a crash in between would leave a row naming an
/// assignment that never actually happened.</para>
///
/// <para><b>Not used by the two `Ago.Chat.Worker` assignment claimers</b>
/// (<c>SkipLockedAssignmentClaimer</c>, <c>RedisLockAssignmentClaimer</c>), on purpose - see their own
/// remarks. They already write everything else about a claim (the capacity compare-and-set, the
/// conversation's own save) as statements issued directly against a connection and transaction they
/// hold explicitly, and adding this port as a second, differently-shaped way to reach the same
/// transaction would mean a future edit could change one path and not the other without either
/// failing to compile - exactly the "a claim commits without its interval" failure this item exists to
/// close off. They write the identical row shape as raw SQL instead, adjacent in the same method as
/// the claim itself.</para>
///
/// <para><b>No read method.</b> `23-03`'s own Scope: "no aggregates and no read model" until a report
/// is measurably slow. The one read this item does need - "how many intervals overlap instant T" - has
/// no real caller yet either (nothing renders it), so it is not part of this port at all; it is a
/// directly-tested Infrastructure query, the same "no caller yet, tested standalone" shape
/// `WaitingConversationClaimQuery` already established for itself.</para>
/// </summary>
public interface IConversationAssignmentLog
{
    /// <summary>Stages a newly-opened interval - synchronous and side-effect-free until the caller's
    /// own <c>SaveChangesAsync</c> runs, because this is an in-memory <c>Add</c> against the change
    /// tracker, not a round trip.</summary>
    void Open(ConversationAssignmentInterval interval);

    /// <summary>Stages <paramref name="endedAt"/> on the interval currently open for
    /// <paramref name="conversationId"/> (<c>EndedAt is null</c>) - at most one such row exists by
    /// construction, since a conversation has at most one operator at a time. A conversation with no
    /// open interval - one assigned before this item shipped, since backfill is explicitly out of
    /// scope (`23-03`'s own Scope) - is a silent no-op: there is nothing to close, and that is the
    /// expected, honest state for it, not an error.</summary>
    Task CloseOpenAsync(ConversationId conversationId, DateTimeOffset endedAt, CancellationToken cancellationToken);
}
