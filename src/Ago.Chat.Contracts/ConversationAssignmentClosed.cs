namespace Ago.Chat.Contracts;

/// <summary>
/// `26-237`/`adr/0186` (operator-load rollup, decision B): one operator's assignment interval closed.
/// The analytics operator-load rollup's own input - the last analytics report still computed live over
/// <c>ago_chat</c> (an O(N²) correlated-overlap query over <c>conversation_assignments</c>, the «По
/// сайту» screen's 499 cause); this event moves it onto the precomputed rollup like every other report.
///
/// <para><b>Why the overlap is resolved <em>here</em>, at close, not at aggregation time.</b> An
/// interval's <see cref="ConcurrentLoad"/> - how many of that operator's intervals overlapped the instant
/// this one began - is an interval-overlap that does not reduce to an additive daily counter, so the
/// aggregator cannot recover it from independent per-event rows. It is exact and cheap to compute once,
/// at close, with the existing <c>ConversationAssignmentOverlapQuery.CountHeldAtAsync</c> (a single
/// <c>count(*)</c> at the interval's start), so AGO Chat stamps it onto this event and the rollup keys on
/// it. This is the operator-load-specific supersession of the design's §7 compute-on-read stance
/// (`adr/0186`): every other metric stays derived-in-the-aggregator; this one is resolved at source
/// because only source has the cheap exact answer.</para>
///
/// <para><b><see cref="OccurredAt"/> is the interval's start, not its close.</b> The operator-load report
/// windows by <c>conversation_assignments.started_at</c> (when the holding period began), so this event's
/// envelope <c>OccurredAt</c> is <see cref="StartedAt"/> - it rolls up into the tenant-local day the
/// interval <em>started</em>, matching the report's window semantics. It is emitted at close (that is when
/// <see cref="EndedAt"/>, <see cref="ConcurrentLoad"/> and <see cref="FirstReplyAt"/> are all known),
/// staged to the outbox in the same transaction as the interval's own close (rule 4).</para>
///
/// <para><b>Facts, not derived scalars, where the raw stream can carry them.</b> <see cref="FirstReplyAt"/>
/// is the instant of the first operator reply within the interval (or null - no reply from this operator
/// during this holding period); the aggregator derives the reply-latency from it minus <see cref="StartedAt"/>,
/// so a future report can redefine "reply latency" without a re-emission. <see cref="ConcurrentLoad"/> and
/// <see cref="Capacity"/> are the exception noted above - resolved at source because they are not otherwise
/// recoverable. An interval that closes with no reply carries a null <see cref="FirstReplyAt"/>, never a
/// sentinel.</para>
///
/// <para>Carries <see cref="SiteId"/> and <see cref="TenantZone"/> (the analytics envelope identifiers the
/// ingest lifts), no message body or personal content on the wire (`messaging.md` / `personal-data.md`) -
/// only ids, instants and the two resolved-at-close integers. Keyed by <see cref="ConversationId"/>;
/// <c>MessageId</c> is a fresh id per publish (an operator can close, be transferred, and hold again, so
/// the conversation id is not a redelivery-idempotency key), `Version` 1.</para>
/// </summary>
public sealed record ConversationAssignmentClosed(
    Guid ConversationId,
    Guid SiteId,
    Guid OperatorId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int ConcurrentLoad,
    int Capacity,
    DateTimeOffset? FirstReplyAt,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    string TenantZone);
