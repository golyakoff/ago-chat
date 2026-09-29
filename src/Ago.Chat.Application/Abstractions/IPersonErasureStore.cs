using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `adr/0189`/`26-275` slice #3: the write behind chat's <c>PersonErased</c> consumer - the calendar
/// erased its own half of a person (Option A, issue 1815) and told chat, via the outbox, to erase the
/// rest: the Person itself, its conversations and everything under them.
///
/// <para><b>A flag, not an immediate delete - the identical shape <see cref="IErasureRequestRepository"/>
/// already establishes for a site or a conversation.</b> A person can own an unbounded amount of history
/// (every conversation, every message, every attachment), so this port only stamps
/// <c>visitors.erasure_requested_at</c>; a background sweep (<c>PersonErasureJob</c>, <c>Ago.Chat.Worker</c>)
/// does the actual bounded, ordered removal off a timer, never inside this call. This mirrors
/// <c>SiteErasureJob</c>'s own "stamp the conversations, wait for them to drain, then remove the parent
/// row" shape one level down: a person's conversations are stamped for <c>ConversationErasureJob</c> the
/// same way a site's are for <c>SiteErasureJob</c>, and the <c>visitors</c> row itself is removed only
/// once none remain (<c>PersonErasureQuery.HasAnyConversationAsync</c>).</para>
///
/// <para><b>Idempotent on the person id (CLAUDE.md rule 5), and never an error for a gone or unknown
/// person.</b> A redelivery of the same <c>PersonErased</c> event - or one that outlives the sweep it
/// triggered - finds the flag already set (a no-op) or the visitor already gone entirely (also a no-op,
/// reported distinctly only so the consumer can log rather than retry). Unlike
/// <see cref="IErasureRequestRepository"/>'s own pair, there is no operator behind this write and no
/// <c>erasure_records</c> receipt minted - the same posture <c>IPersonRegistrationStore</c>'s own remarks
/// state for the opposite direction (<c>PersonRegistered</c>): this is a trusted internal event between
/// two products, not an operator-initiated request with an audit trail of "who asked for this."</para>
///
/// <para>Scoped to <c>accountId</c> as well as <c>personId</c> - the identical "a row that exists but
/// belongs to a different tenant is indistinguishable from one that does not exist at all" defence
/// <see cref="IErasureRequestRepository.RequestConversationErasureAsync"/>'s own remarks give for its own
/// <c>siteId</c> parameter, applied here to a cross-product event rather than an operator's own request.</para>
/// </summary>
public interface IPersonErasureStore
{
    /// <returns><see cref="PersonErasureOutcome.Requested"/> when this call set the flag for the first
    /// time, <see cref="PersonErasureOutcome.AlreadyRequested"/> when it was already set (a redelivery,
    /// or a sweep already under way), <see cref="PersonErasureOutcome.UnknownPerson"/> when no visitor
    /// under this id exists for this account at all - never erased, already fully erased, or simply
    /// never registered here. All three are clean no-ops from the caller's point of view; only the third
    /// is worth a distinct log line, since nothing about waiting would ever change it.</returns>
    Task<PersonErasureOutcome> RequestErasureIfPresentAsync(
        SiteId accountId, VisitorId personId, DateTimeOffset occurredAt, CancellationToken cancellationToken);
}

public enum PersonErasureOutcome
{
    Requested,
    AlreadyRequested,
    UnknownPerson,
}
