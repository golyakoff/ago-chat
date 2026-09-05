using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-17`: "how many conversations each operator held, how many of those were additional, and what
/// their response times were against the load they were carrying at the time" (the backlog item's own
/// Goal) - a read over `conversation_assignments` joined to `conversations` and `messages`, per
/// operator and per range. A new port rather than a fifth method on
/// <see cref="IOperatorAnalyticsReadStore"/>: that store's own remarks already draw this line for
/// <c>IConversionReportReadStore</c>/<c>ITagBreakdownReadStore</c>/<c>IModuleFlowReadStore</c> - "a
/// genuinely different query shape" gets its own port. This one is a genuinely different shape twice
/// over: it is windowed by <c>conversation_assignments.started_at</c> (when an operator's own holding
/// period began), not `conversations.created_at`, and every number on it is derived from interval
/// overlap against `operators.capacity` - a join <see cref="IOperatorAnalyticsReadStore"/>'s own query
/// never performs and has no reason to.
///
/// <para><b>"Held", not "attributed to".</b> <see cref="IOperatorAnalyticsReadStore"/>'s own
/// per-operator numbers credit whoever replied first, even after a transfer moves the conversation
/// elsewhere (that store's own remarks). This port answers a different question: which operator's own
/// assignment interval this is, full stop - an operator who took a conversation, was transferred it
/// away, and had it transferred back holds it across <b>two</b> intervals, and both belong to this
/// operator's own numbers here even though only one of them may include the reply that
/// <see cref="IOperatorAnalyticsReadStore"/> would credit to them. This is <c>23-17</c>'s own
/// Done-when, stated precisely: "a conversation an operator held twice... is counted once as a
/// conversation and twice as an interval" - see <see cref="OperatorLoadSummary.ConversationsHeld"/>
/// versus <see cref="OperatorLoadSummary.IntervalsHeld"/>.</para>
///
/// <para><b>"Additional", computed, never stored.</b> `docs/design/decisions.md` §2's naming
/// amendment: an interval is <b>additional</b> when the operator was already carrying
/// <see cref="Domain.Operator.Capacity"/> conversations, counting this one, the instant it started -
/// the identical overlap `ConversationAssignmentOverlapQuery.CountHeldAtAsync` proves against a known
/// fixture, applied here to every interval in the window rather than to one caller-supplied instant.
/// A concurrent load of exactly <c>Capacity</c> is <b>standard</b> - it fills the last open slot,
/// it does not exceed it; only a load strictly greater than <c>Capacity</c> is additional, the same
/// "the second only happens once capacity is full" reading `23-03`'s own Naming section states. No
/// column on `conversation_assignments` names this - <see cref="Domain.ConversationAssignmentSource"/>
/// still has exactly two members, `Assigned`/`Transferred`, and this port reads no third or fourth.
/// </para>
///
/// <para><b>Response time, per interval, per load bucket.</b> For an interval
/// <c>[started_at, ended_at)</c>, "time to first operator reply" is the first message this same
/// operator sent, in this same conversation, no earlier than <c>started_at</c> and (if the interval
/// has closed) before <c>ended_at</c> - never a message from a different operator who also touched the
/// conversation, and never a message outside this specific holding period. An interval with no such
/// message contributes nothing to any average, the identical "nothing to average yet, never zero, never
/// a sentinel" discipline <see cref="OperatorAnalyticsBucket.AverageFirstResponseSeconds"/> already
/// applies for a materially different reason (there, no reply ever came; here, a reply may exist just
/// not from *this* operator during *this* interval - the same rule, two different ways the underlying
/// fact can be true). The concurrent load a reply is bucketed under is the load computed at the
/// interval's own <c>started_at</c> - "the moment that reply was owed" (the backlog item's own Goal),
/// since that is when this operator's own clock on this conversation starts, not the load at the
/// instant they happened to type.</para>
///
/// <para><b>Not a rate, so <see cref="AnalyticsOptions.MinimumSampleForRate"/> does not apply here.</b>
/// Every number <see cref="OperatorLoadSummary"/> carries is a count or a duration average, the same
/// "a duration average built on one interval is a real number about that one interval, not a fraction
/// that misrepresents a population" reasoning <see cref="IOperatorAnalyticsReadStore"/>'s own remarks
/// already give for <see cref="OperatorAnalyticsBucket.AverageDurationSeconds"/> - there is no
/// thin-denominator ranking hazard for the threshold to guard, and this report never ranks operators
/// against each other at all (the backlog item's own Out-of-scope). <see cref="OperatorLoadSummary.ByLoad"/>
/// is ordered by bucket ascending, a stable listing, never a ranking.</para>
///
/// <para><b>Not a caching concern</b> (`CLAUDE.md` rule 8, `caching.md`), for the identical reason
/// <see cref="IOperatorAnalyticsReadStore"/> is none: pure observability for a human reading a report,
/// at human frequency, feeding no write decision anywhere.</para>
/// </summary>
public interface IOperatorLoadReportReadStore
{
    /// <summary><paramref name="from"/> is inclusive, <paramref name="to"/> is exclusive - the same
    /// half-open convention every window in this codebase already uses, applied here to
    /// `conversation_assignments.started_at` rather than to a conversation's own `created_at`. Returns
    /// one entry per operator who started holding at least one conversation in the window - never a
    /// zero-filled row for an operator with no interval in it, the same "no manufactured row" rule
    /// <see cref="OperatorAnalyticsResult.ByOperator"/> already holds.</summary>
    Task<IReadOnlyList<OperatorLoadSummary>> GetOperatorLoadReportAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

/// <summary>One operator's load summary over the report's window - see
/// <see cref="IOperatorLoadReportReadStore"/> for exactly what each number counts and why.</summary>
/// <param name="Operator">The operator these intervals belong to.</param>
/// <param name="OperatorName">`23-02`'s own display name, <see langword="null"/> for an operator who
/// predates it - the same fallback <see cref="OperatorAnalyticsOperatorBucket.OperatorName"/> already
/// uses.</param>
/// <param name="ConversationsHeld">Distinct conversations this operator held at least one interval of
/// in the window - a conversation transferred away and back counts once here.</param>
/// <param name="IntervalsHeld">Every assignment interval this operator held in the window - the same
/// transferred-away-and-back conversation counts twice here. <see cref="IntervalsHeld"/> is never less
/// than <see cref="ConversationsHeld"/>, and the two differ exactly when a transfer returned a
/// conversation to an operator who had already held it once.</param>
/// <param name="StandardIntervals">Intervals where this operator's own concurrent load, counting the
/// interval itself, did not exceed their capacity at the time.</param>
/// <param name="AdditionalIntervals">Intervals where it did.
/// <see cref="StandardIntervals"/> + <see cref="AdditionalIntervals"/> == <see cref="IntervalsHeld"/>
/// always.</param>
/// <param name="ByLoad">Response time bucketed by the operator's own concurrent load when each interval
/// started - see <see cref="IOperatorLoadReportReadStore"/>'s remarks. Ordered by bucket ascending;
/// never a bucket with zero intervals in it.</param>
public sealed record OperatorLoadSummary(
    OperatorId Operator,
    string? OperatorName,
    long ConversationsHeld,
    long IntervalsHeld,
    long StandardIntervals,
    long AdditionalIntervals,
    IReadOnlyList<OperatorLoadBucketEntry> ByLoad);

/// <param name="BucketLabel"><see cref="OperatorLoadBuckets.Label"/>'s own output for this bucket -
/// computed once by the read store from <see cref="AnalyticsOptions.LoadBucketUpperBounds"/>, not
/// recomputed by any caller.</param>
/// <param name="IntervalCount">How many intervals in the window started at this bucket's own load.
/// </param>
/// <param name="ReplyCount">How many of those intervals ever saw a reply from the operator who held
/// them - the denominator <see cref="AverageFirstReplySeconds"/> is averaged over. Can be less than
/// <see cref="IntervalCount"/>: an interval this operator held but never replied in (transferred away
/// first, or the conversation closed unanswered) contributes to the bucket's count but not its
/// average.</param>
/// <param name="AverageFirstReplySeconds"><see langword="null"/> when <see cref="ReplyCount"/> is zero -
/// never zero itself, the same "nothing to average yet" rule
/// <see cref="OperatorAnalyticsBucket.AverageFirstResponseSeconds"/> already applies.</param>
public sealed record OperatorLoadBucketEntry(
    string BucketLabel, long IntervalCount, long ReplyCount, double? AverageFirstReplySeconds);
