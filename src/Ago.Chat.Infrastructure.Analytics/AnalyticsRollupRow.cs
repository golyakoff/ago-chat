namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-223`/`adr/0186` §7: one row of the grouped range scan over <c>analytics_daily_rollups</c> - a
/// <c>(dimension_type, dimension_key)</c> pair with its metrics summed across every <c>local_day</c> in the
/// requested window. Averages are stored <b>decomposed</b> (sum + count) so they stay additive across days;
/// this row carries the summed components and <see cref="RollupOperatorAnalyticsReadStore"/> reconstitutes
/// the ratio - never the store (design §7: "the read reconstitutes the ratio, never the store").
/// </summary>
/// <param name="DimensionType">One of <see cref="RollupDimensionTypes"/>' values.</param>
/// <param name="DimensionKey">The id / label / host / empty-string-for-total this dimension is keyed by.</param>
/// <param name="ConversationCount">Conversations started on this dimension's local days, summed.</param>
/// <param name="MissedCount">Closed-with-no-operator-message conversations, summed.</param>
/// <param name="FirstResponseSecondsSum">Numerator of the first-response-seconds average.</param>
/// <param name="FirstResponseCount">Denominator of the first-response-seconds average (conversations that
/// received an operator reply).</param>
/// <param name="DurationSecondsSum">Numerator of the duration-seconds average.</param>
/// <param name="DurationCount">Denominator of the duration-seconds average (closed conversations).</param>
internal sealed record AnalyticsRollupRow(
    string DimensionType,
    string DimensionKey,
    long ConversationCount,
    long MissedCount,
    long FirstResponseSecondsSum,
    long FirstResponseCount,
    long DurationSecondsSum,
    long DurationCount);
