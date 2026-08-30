namespace Ago.Chat.Contracts;

/// <summary>
/// `18-11`: `GET /api/v1/conversations/tag-breakdown-report`'s response body. <see cref="From"/>/
/// <see cref="To"/> are the bound this report actually used - always present, the same "the bound is
/// visible, not silent" shape `OperatorAnalyticsResponse.From`/`To` already establishes.
///
/// <b>The percentage-tagged figure is not decoration - render it beside the breakdown, every time.</b>
/// See `Ago.Chat.Application.Abstractions.ITagBreakdownReadStore`'s own remarks (`ago-chat`) for the full
/// reasoning: this number is how a reader knows how much of the window <see cref="ByTag"/> actually
/// covers, and hiding it when it looks bad would let an incomplete breakdown read as a complete one.
///
/// <b><see cref="ByTag"/>'s own counts will not sum to <see cref="TotalConversationCount"/> - say so
/// wherever this renders.</b> A conversation with more than one tag counts once per tag it holds.
/// </summary>
public sealed record TagBreakdownReportResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    long TotalConversationCount,
    long TaggedConversationCount,
    double? PercentageTagged,
    IReadOnlyList<TagBreakdownBucketDto> ByTag);

/// <param name="TagId">The tag's own stable identity, unaffected by a later rename.</param>
/// <param name="TagName">The tag's current display name.</param>
/// <param name="ConversationCount">Conversations in the window carrying this tag - counted once per tag,
/// not deduplicated against any other tag the same conversation might also hold.</param>
/// <param name="ConvertedCount">This tag's own conversations recorded as <c>Converted</c>.</param>
/// <param name="NotConvertedCount">This tag's own conversations recorded as <c>NotConverted</c>.</param>
/// <param name="RecordedCount"><paramref name="ConvertedCount"/> + <paramref name="NotConvertedCount"/> -
/// the exact denominator <paramref name="ConversionRate"/> is computed over.</param>
/// <param name="ConversionRate"><see langword="null"/> when <paramref name="RecordedCount"/> is zero -
/// never zero itself, the same convention `ConversionBucketDto.ConversionRate` already establishes.
/// </param>
public sealed record TagBreakdownBucketDto(
    Guid TagId,
    string TagName,
    long ConversationCount,
    long ConvertedCount,
    long NotConvertedCount,
    long RecordedCount,
    double? ConversionRate);
