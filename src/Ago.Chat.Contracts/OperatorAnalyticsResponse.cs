namespace Ago.Chat.Contracts;

/// <summary>
/// `18-08`: `GET /api/v1/conversations/analytics`'s response body. <see cref="From"/>/<see cref="To"/>
/// are the bound this report actually used - always present, even when the caller supplied neither and
/// the handler defaulted them, the same "the bound is visible, not silent" shape
/// `SearchConversationsResponse.SearchedFrom`/`SearchedTo` already establishes for `18-01`.
/// </summary>
public sealed record OperatorAnalyticsResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    OperatorAnalyticsBucketDto Overall,
    IReadOnlyList<OperatorAnalyticsChannelBucketDto> ByChannel);

/// <param name="AverageFirstResponseSeconds"><see langword="null"/> when nothing in this bucket ever
/// received an operator reply - see <c>IOperatorAnalyticsReadStore</c>'s own remarks for the exact
/// definition and why a missed conversation is excluded from the average rather than inflating
/// it.</param>
public sealed record OperatorAnalyticsBucketDto(
    long ConversationCount, double? AverageFirstResponseSeconds, long MissedCount);

/// <param name="Channel">One of `Max`/`Sms`/`Telegram`/`WhatsApp` (<c>Ago.Chat.Domain.ChannelKind</c>'s
/// own member names) or the literal <c>"Widget"</c> for a visitor with no external channel identity at
/// all.</param>
public sealed record OperatorAnalyticsChannelBucketDto(string Channel, OperatorAnalyticsBucketDto Bucket);
