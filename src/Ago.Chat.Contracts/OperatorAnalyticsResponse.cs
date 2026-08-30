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
    IReadOnlyList<OperatorAnalyticsChannelBucketDto> ByChannel,
    IReadOnlyList<OperatorAnalyticsOperatorBucketDto> ByOperator);

/// <param name="AverageFirstResponseSeconds"><see langword="null"/> when nothing in this bucket ever
/// received an operator reply - see <c>IOperatorAnalyticsReadStore</c>'s own remarks for the exact
/// definition and why a missed conversation is excluded from the average rather than inflating
/// it.</param>
/// <param name="AverageDurationSeconds">`18-13`: how long a conversation in this bucket takes from
/// start to close, averaged. <see langword="null"/> when nothing in this bucket has closed yet - a
/// conversation still open is excluded from the average entirely, not counted as zero seconds or as
/// "still running" time (<c>IOperatorAnalyticsReadStore</c>'s own remarks, `ago-chat`).</param>
public sealed record OperatorAnalyticsBucketDto(
    long ConversationCount, double? AverageFirstResponseSeconds, double? AverageDurationSeconds, long MissedCount);

/// <param name="Channel">One of `Max`/`Sms`/`Telegram`/`WhatsApp` (<c>Ago.Chat.Domain.ChannelKind</c>'s
/// own member names) or the literal <c>"Widget"</c> for a visitor with no external channel identity at
/// all.</param>
public sealed record OperatorAnalyticsChannelBucketDto(string Channel, OperatorAnalyticsBucketDto Bucket);

/// <summary>
/// `18-09`: one operator's bucket. <see cref="OperatorId"/> is the operator this window's numbers
/// attribute to - the one who replied first, or (only for a conversation nobody ever replied to) the
/// one holding it when it closed unanswered; see <c>IOperatorAnalyticsReadStore</c>'s own remarks
/// (`ago-chat`) for the full reasoning, including why a conversation transferred after being answered
/// still credits whoever answered it, never whoever it was transferred to. The console has no operator
/// display name to render - <c>Ago.Chat.Domain.Operator</c> carries none today - so this is the raw id,
/// the same "no name, so the id itself" precedent `AdminConversationsPage`'s own assigned-operator
/// column already sets on the console side.
/// </summary>
public sealed record OperatorAnalyticsOperatorBucketDto(Guid OperatorId, OperatorAnalyticsBucketDto Bucket);
