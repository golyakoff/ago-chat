namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-08`: the answer to "how am I doing" for one site over one caller-supplied window - a site-wide
/// summary plus the same three numbers broken down per channel. A plain projection, not an aggregate
/// (the same "a read store returns rows, not aggregates" shape <see cref="SiteOverviewItem"/> and
/// <see cref="ConversationSummaryItem"/> already established) - nothing here is loaded through
/// <c>Conversation</c>, and nothing here is written back.
/// </summary>
/// <param name="Overall">Every conversation in the window, regardless of channel.</param>
/// <param name="ByChannel">One entry per channel that had at least one conversation in the window -
/// never a zero-filled row for a channel nobody used (<see cref="IOperatorAnalyticsReadStore"/>'s own
/// remarks on why the query does not manufacture one).</param>
/// <param name="ByOperator">`18-09`: one entry per operator this window's conversations attribute to -
/// see <see cref="IOperatorAnalyticsReadStore"/>'s remarks for exactly what "attribute to" means and
/// why. Never a zero-filled row for an operator with nothing attributed to them, the same "no
/// manufactured row" rule <see cref="ByChannel"/> already holds.</param>
public sealed record OperatorAnalyticsResult(
    OperatorAnalyticsBucket Overall,
    IReadOnlyList<OperatorAnalyticsChannelBucket> ByChannel,
    IReadOnlyList<OperatorAnalyticsOperatorBucket> ByOperator);

/// <summary>
/// One bucket's worth of the three numbers this item exists to compute. See
/// <see cref="IOperatorAnalyticsReadStore"/> for the exact definitions of "first response" and
/// "missed" - they are stated once, at the port, so a reader who lands on either side of it (the SQL
/// or a caller) finds the same rule.
/// </summary>
/// <param name="ConversationCount">Conversations started in the window, counted by
/// <c>conversations.created_at</c> - not by any message's timestamp, so a conversation that received
/// its first reply after the window closed is still counted once, in the window it started.</param>
/// <param name="AverageFirstResponseSeconds"><see langword="null"/> when no conversation in this
/// bucket ever received an operator reply - never zero, and never a value inflated by the conversations
/// that make up <paramref name="MissedCount"/> (those are excluded from the average entirely, not
/// counted as some large number).</param>
/// <param name="AverageDurationSeconds">`18-13`: how long a conversation in this bucket takes from
/// <c>CreatedAt</c> to <c>ClosedAt</c>, averaged. <see langword="null"/> when nothing in this bucket has
/// closed yet - the same "nothing to average yet" rule <paramref name="AverageFirstResponseSeconds"/>
/// already applies to its own null case. A conversation still open contributes nothing to this average;
/// it is not treated as zero seconds, and not as "now minus created" either - that would make the
/// average keep drifting for the exact same historical window purely because time passed since the
/// report last ran.</param>
/// <param name="MissedCount">Conversations that were <c>Closed</c> with no operator message ever sent
/// in them. A conversation still <c>Waiting</c>/<c>Assigned</c> with no reply yet is not "missed" by
/// this definition - it has not been given the chance to be, and counting it would conflate "closed
/// with nobody home" with "thirty seconds old".</param>
public sealed record OperatorAnalyticsBucket(
    long ConversationCount, double? AverageFirstResponseSeconds, double? AverageDurationSeconds, long MissedCount);

/// <param name="Channel">The CLR member name of <see cref="Domain.ChannelKind"/> the conversation's
/// visitor was first reached through, or the literal <c>"Widget"</c> for a visitor with no
/// <see cref="Domain.ChannelIdentity"/> row at all - <see cref="Domain.ChannelKind"/> itself
/// deliberately has no <c>Widget</c> member (see that type's own remarks), so this is a read-time label,
/// not a domain value.</param>
public sealed record OperatorAnalyticsChannelBucket(string Channel, OperatorAnalyticsBucket Bucket);

/// <summary>`18-09`: one operator's bucket - see <see cref="IOperatorAnalyticsReadStore"/> for exactly
/// which conversations attribute to <paramref name="Operator"/> and why.</summary>
public sealed record OperatorAnalyticsOperatorBucket(Domain.OperatorId Operator, OperatorAnalyticsBucket Bucket);
