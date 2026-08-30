namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Dapper's raw row shape for <see cref="TagBreakdownReadStore"/>'s first statement - the
/// site-wide tagging coverage, always exactly one row (a plain `count(*)`-shaped query never returns
/// zero rows the way a `GROUPING SETS` query over zero input rows does, so this class needs no
/// empty-result substitution the way `OperatorAnalyticsReadStore` does for its own overall row).</summary>
internal sealed record TagBreakdownOverallRow(long TotalConversationCount, long TaggedConversationCount);
