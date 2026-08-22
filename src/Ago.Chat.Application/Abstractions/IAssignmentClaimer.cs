using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `4-03`: the exclusive-claim-attempt step behind one port, two implementations -
/// `concurrency.md`'s mechanism A (`SELECT ... FOR UPDATE SKIP LOCKED`, `4-02`) and mechanism B
/// (a per-operator Redis lock, `4-03`). Both attempt to assign up to <paramref name="batchSize"/>
/// waiting conversations for one site and return how many actually got assigned - the lock (or the
/// database row lock) only ever decides who gets to *attempt* a claim; the atomic capacity
/// compare-and-set (`IOperatorCapacity`) and the `Conversation` aggregate's own optimistic-
/// concurrency save are what make the outcome correct under either mechanism (`adr/0009`,
/// `CLAUDE.md` rule 8 - never cache what a write decision depends on).
/// </summary>
public interface IAssignmentClaimer
{
    Task<int> AssignWaitingConversationsAsync(SiteId siteId, int batchSize, CancellationToken cancellationToken);
}
