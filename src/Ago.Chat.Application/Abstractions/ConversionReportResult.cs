namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-10`: the answer to "how much benefit is this business actually getting" for one site over one
/// caller-supplied window - a site-wide bucket plus the same numbers broken down per operator, the
/// identical two-level shape `18-09` added to <see cref="OperatorAnalyticsResult"/>. A plain projection,
/// not an aggregate - nothing here is loaded through <see cref="Domain.Conversation"/>, matching that
/// type's own "read store returns rows, not aggregates" precedent.
///
/// <para><b>This is real, and it is not a sales-verified number - say so wherever this is rendered.</b>
/// See <see cref="Domain.ConversationOutcome"/>'s own remarks for the full reasoning: every value here
/// is built from what an operator chose to record, not from anything AGO Chat independently verified.
/// <see cref="ConversionBucket.RecordedCount"/> exists specifically so a caller can show how much of the
/// window this rate is even based on, rather than presenting a rate with no sense of its own coverage.</para>
/// </summary>
/// <param name="Overall">Every conversation in the window, regardless of who (if anyone) it is
/// attributed to.</param>
/// <param name="ByOperator">One entry per operator who has at least one conversation with a real
/// outcome recorded (`Converted`/`NotConverted`/`FollowUpNeeded`) in the window - never a zero-filled
/// row for an operator with nothing recorded, the same "no manufactured row" rule
/// <see cref="OperatorAnalyticsResult.ByOperator"/> already holds.</param>
public sealed record ConversionReportResult(
    ConversionBucket Overall, IReadOnlyList<ConversionOperatorBucket> ByOperator);

/// <summary>
/// One bucket's worth of outcome counts and the rate computed from them. See
/// <see cref="IConversionReportReadStore"/> for the query that fills this in.
/// </summary>
/// <param name="ConvertedCount">Conversations in this bucket recorded as <c>Converted</c>.</param>
/// <param name="NotConvertedCount">Conversations in this bucket recorded as <c>NotConverted</c>.</param>
/// <param name="FollowUpNeededCount">Conversations in this bucket recorded as <c>FollowUpNeeded</c> -
/// counted in neither half of <see cref="ConversionRate"/>'s own numerator or denominator
/// (<see cref="Domain.ConversationOutcome.FollowUpNeeded"/>'s own remarks: it is not yet known whether
/// this will convert).</param>
/// <param name="UnsetCount"><b>The load-bearing count this report exists to keep visible.</b>
/// Conversations nobody has recorded an outcome for at all - excluded from <see cref="ConversionRate"/>'s
/// denominator entirely, not folded into <see cref="NotConvertedCount"/> and not silently dropped either.
/// A high <see cref="UnsetCount"/> relative to <see cref="RecordedCount"/> is itself the signal that
/// <see cref="ConversionRate"/> is based on thin, voluntarily-reported coverage - the report's own UI is
/// the place that number belongs, not just this doc comment.</param>
/// <param name="RecordedCount">derived convenience: <see cref="ConvertedCount"/> +
/// <see cref="NotConvertedCount"/> - the exact denominator <see cref="ConversionRate"/> is computed
/// over, surfaced separately so a reader is never left doing that arithmetic themselves to understand
/// how much data the rate rests on.</param>
/// <param name="ConversionRate"><see langword="null"/> when <see cref="RecordedCount"/> is zero - never
/// zero itself, and never a rate inflated or deflated by <see cref="FollowUpNeededCount"/> or
/// <see cref="UnsetCount"/> (both excluded from the denominator, the backlog item's own stated,
/// load-bearing decision: conflating "operators chose not to convert this" with "nobody has recorded an
/// outcome yet" would make the rate meaningless the moment adoption of the console control is anything
/// less than universal).</param>
public sealed record ConversionBucket(
    long ConvertedCount, long NotConvertedCount, long FollowUpNeededCount, long UnsetCount,
    long RecordedCount, double? ConversionRate);

/// <summary>`18-10`'s own per-operator breakdown - see <see cref="IConversionReportReadStore"/> for
/// which conversations attribute to <paramref name="Operator"/> and why (a genuinely simpler question
/// than `18-09`'s first-reply attribution: there is no "who deserves credit" ambiguity to resolve
/// here).</summary>
public sealed record ConversionOperatorBucket(Domain.OperatorId Operator, ConversionBucket Bucket);
