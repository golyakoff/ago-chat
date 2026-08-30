using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-08`: the read-side port behind the console's own "how am I doing" panel - hand-written SQL over
/// the write model, never through an aggregate (`adr/0004`), the same mechanism every other read model
/// in this codebase uses. A new port rather than a fourth method on <see cref="IConversationReadStore"/>:
/// that store answers "give me these rows" (history, delta, a page of conversations), while this one
/// answers "compute these aggregates" - a genuinely different query shape, the same reason `12-02` got
/// its own <see cref="IPlatformOverviewReadStore"/> instead of a fifth method here.
///
/// <para><b>This is explicitly not `12-02`.</b> <see cref="IPlatformOverviewReadStore"/> is the one
/// cross-tenant read in this codebase (`tenant-isolation.md`); this port takes a <see cref="SiteId"/>
/// and its `WHERE` clause cannot address another tenant's rows - an ordinary site-scoped read, gated the
/// same way <see cref="GetAllForSiteAsync"/> style queries already are (the caller,
/// <c>GetOperatorAnalyticsForSiteHandler</c>, is what actually checks the permission).</para>
///
/// <para><b>"First response time", stated precisely.</b> For a conversation that received at least one
/// operator message, it is the time from that conversation's first <em>visitor</em> message to its
/// first <em>operator</em> message - never from the conversation's own <c>created_at</c>, because
/// nothing guarantees the visitor's first message lands in the same instant the conversation was
/// started. A conversation with no operator message at all contributes nothing to the average - it is
/// not treated as an enormous or infinite response time, which would silently skew every other
/// conversation's average toward whatever sentinel was chosen. It is counted instead in
/// <see cref="OperatorAnalyticsBucket.MissedCount"/>, and only there.</para>
///
/// <para><b>"Missed", stated precisely, and why this reading and not the other one.</b> A conversation
/// is missed if it is <c>Closed</c> and never received an operator message. The alternative considered
/// - "no operator reply within some fixed window, regardless of state" - was rejected because it needs
/// an SLA threshold this item has no basis for choosing: CLAUDE.md rule 7 bans invented numbers, and no
/// number this codebase has ever measured says what "too slow" means for AGO Chat. The state-based
/// reading needs no such number and answers the question the backlog item's own "Open questions"
/// actually poses - a conversation still <c>Waiting</c>/<c>Assigned</c> thirty seconds old has simply not
/// had its chance yet, and is excluded from both the miss count and the response-time average until it
/// resolves one way or the other.</para>
///
/// <para><b>Per-channel attribution, and its one known limitation.</b> A conversation carries no
/// <see cref="Domain.ChannelKind"/> of its own - only its visitor does, indirectly, through zero or more
/// <see cref="Domain.ChannelIdentity"/> rows (`14-01`). A visitor reached by more than one channel (the
/// same person messaging by SMS and by MAX - <see cref="Domain.ChannelIdentity"/>'s own remarks say this
/// is real and representable) has more than one such row, and nothing in the schema says which channel
/// "belongs" to any one of that visitor's conversations. This query picks the visitor's
/// <em>earliest-linked</em> channel identity - the channel that first brought them into contact - as a
/// deterministic, stated tiebreak, not a guess. A visitor with no channel identity at all (every widget
/// visitor, by <see cref="Domain.ChannelKind"/>'s own design) is labelled <c>"Widget"</c>. Getting this
/// exactly right for a visitor who switches channels mid-relationship needs a per-conversation channel
/// column this item's own scope explicitly does not add; the tiebreak is the honest answer available
/// from data already recorded, not a claim that it is the only correct one.</para>
///
/// <para><b>`18-09`: per-operator attribution, and the ambiguity that item's own backlog file names
/// explicitly.</b> A conversation's <see cref="Domain.Conversation.OperatorId"/> is whoever is
/// <em>currently</em> assigned - it moves on every `18-02` <c>TransferTo</c>, so the operator who
/// answered a conversation and the operator now holding it can be two different people. This query
/// attributes every number to <b>the operator who actually replied first</b> - the first
/// <c>author_id</c> among that conversation's operator-authored messages - never the currently assigned
/// one. Concretely: operator A answers, the conversation is transferred to operator B, nobody else ever
/// writes again - the conversation counts toward A's <see cref="OperatorAnalyticsBucket.ConversationCount"/>
/// and <see cref="OperatorAnalyticsBucket.AverageFirstResponseSeconds"/>, never B's, exactly as though
/// the transfer had not happened. The alternative (attribute to whoever holds the conversation right
/// now) was rejected because it would let a transfer silently reassign credit for work someone else
/// did - the same "a compare-and-set fact belongs to whoever actually produced it" reasoning
/// <c>MarkReadByOperator</c>'s own per-operator watermark already applies to a different but structurally
/// similar case.</para>
///
/// <para>A conversation nobody has ever answered has no "operator who replied" to attribute to. Rather
/// than dropping it from every operator's numbers - which would make
/// <see cref="OperatorAnalyticsBucket.MissedCount"/> read `0` for every operator, always, since a
/// conversation only ever enters that count by never having a reply - this query falls back to
/// <see cref="Domain.Conversation.OperatorId"/> (the currently/last-assigned operator) for exactly this
/// one case: the only operator who can honestly be said to have missed it is the one who was holding it
/// when it closed unanswered. A conversation closed while still <c>Waiting</c> - never assigned to
/// anyone at all - has no operator to fall back to either, and is excluded from
/// <see cref="OperatorAnalyticsResult.ByOperator"/> entirely, the same "no data to attribute, name the
/// gap rather than invent a placeholder" precedent the channel tiebreak above already sets.</para>
///
/// <para><b>Not a caching concern</b> (`CLAUDE.md` rule 8, `caching.md`). Nothing this query returns
/// feeds a write, a compare-and-set, or a capacity check anywhere in the system - it is pure
/// observability for a human reading a panel, the same category `12-02`'s owner overview occupies. No
/// cache is added for the same reason that one has none: one query, run by one site's own operator, at
/// human frequency.</para>
/// </summary>
public interface IOperatorAnalyticsReadStore
{
    /// <summary>
    /// <paramref name="from"/> is inclusive, <paramref name="to"/> is exclusive - the same half-open
    /// convention `IPlatformOverviewReadStore.ListSitesAsync`'s window parameter documents, applied to
    /// both ends here because the caller supplies both rather than the query deriving one from "now".
    /// Conversations are selected by <c>conversations.created_at</c> falling in that range; every
    /// aggregate computed for a selected conversation reads its <em>whole</em> message history, not
    /// only the messages that also fall inside the window - a conversation started two minutes before
    /// the window closes and answered five minutes after it still has a real, reportable first-response
    /// time.
    /// </summary>
    Task<OperatorAnalyticsResult> GetSiteAnalyticsAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}
