namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Dapper's raw row shape for <see cref="TagBreakdownReadStore"/>'s second statement - one row
/// per tag that tagged at least one conversation in the window. No `grouping()` disambiguator, unlike
/// <see cref="OperatorAnalyticsRow"/>/<see cref="ConversionReportRow"/>: this is a plain `group by t.id,
/// t.name`, not a `GROUPING SETS` query, so every row this produces is a real per-tag bucket - see
/// <see cref="TagBreakdownReadStore"/>'s own class remarks for why this query needs no total row of its
/// own (the first statement already computes one, over a genuinely different row set).</summary>
internal sealed record TagBreakdownRow(
    Guid TagId, string TagName, long ConversationCount, long ConvertedCount, long NotConvertedCount);
