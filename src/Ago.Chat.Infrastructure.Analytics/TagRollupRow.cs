namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-237`/`adr/0186` §7: one grouped row of the tag-breakdown read over <c>analytics_daily_rollups</c> -
/// a <c>(dimension_type, dimension_key)</c> pair with its measures summed across the requested window. Two
/// dimensions are read: the <c>total</c> row supplies the site-wide coverage (all conversations and the
/// distinct-tagged subset), and each <c>tag</c> row supplies one tag's per-tag counts. The
/// distinct-tagged-conversation coverage figure lives on the <c>total</c> row as
/// <see cref="TaggedConversationCount"/> (the `26-237` rollup measure) because summing the per-tag
/// <see cref="ConversationCount"/> would double-count a conversation carrying several tags.
/// </summary>
/// <param name="DimensionType">One of <see cref="RollupDimensionTypes"/>' values - here always
/// <see cref="RollupDimensionTypes.Total"/> or <see cref="RollupDimensionTypes.Tag"/>.</param>
/// <param name="DimensionKey">Empty string for the total row; the tag id as text for a tag row.</param>
/// <param name="ConversationCount">On the total row, all conversations in the window; on a tag row, the
/// conversations carrying that tag (once per tag), summed across the window.</param>
/// <param name="TaggedConversationCount">On the total row, conversations carrying at least one tag, counted
/// once each - the report's <c>TaggedConversationCount</c>. Meaningless on a tag row (equals that row's own
/// conversation count) and not read there.</param>
/// <param name="ConvertedCount">This slice's conversations recorded <c>Converted</c>, summed - on a tag row,
/// that tag's own.</param>
/// <param name="NotConvertedCount">This slice's conversations recorded <c>NotConverted</c>, summed.</param>
internal sealed record TagRollupRow(
    string DimensionType,
    string DimensionKey,
    long ConversationCount,
    long TaggedConversationCount,
    long ConvertedCount,
    long NotConvertedCount);
