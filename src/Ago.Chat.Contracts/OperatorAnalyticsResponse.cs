namespace Ago.Chat.Contracts;

/// <summary>
/// `18-08`: `GET /api/v1/conversations/analytics`'s response body. <see cref="From"/>/<see cref="To"/>
/// are the bound this report actually used - always present, even when the caller supplied neither and
/// the handler defaulted them, the same "the bound is visible, not silent" shape
/// `SearchConversationsResponse.SearchedFrom`/`SearchedTo` already establishes for `18-01`.
/// </summary>
/// <param name="ByReferrer">`18-12`: one entry per referrer host, plus a <c>"Direct"</c> entry for every
/// conversation whose visitor carried none - this is what the browser reported (`document.referrer`'s
/// host), never a verified fact; the console's own copy says so.</param>
/// <param name="ByCampaign">`18-12`: one entry per `utm_campaign` value actually seen on a landing URL -
/// never a "no campaign" row, matching <see cref="ByOperator"/>'s own "nobody assigned" exclusion.
/// Equally unverified - a client-supplied query parameter, not a confirmed fact.</param>
/// <param name="PreviousFrom">`23-16`: the immediately preceding window of equal length's own start -
/// see <c>Ago.Chat.Application.Abstractions.PrecedingPeriod</c>.</param>
/// <param name="PreviousTo">`23-16`: that window's own end.</param>
/// <param name="PreviousOverall">`23-16`: <see cref="Overall"/>'s identical shape, computed over the
/// preceding window - never a per-channel/per-operator/per-referrer/per-campaign breakdown of it (the
/// item's own scope: the headline figure gets a comparison, not every row of every table).</param>
public sealed record OperatorAnalyticsResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    OperatorAnalyticsBucketDto Overall,
    DateTimeOffset PreviousFrom,
    DateTimeOffset PreviousTo,
    OperatorAnalyticsBucketDto PreviousOverall,
    IReadOnlyList<OperatorAnalyticsChannelBucketDto> ByChannel,
    IReadOnlyList<OperatorAnalyticsOperatorBucketDto> ByOperator,
    IReadOnlyList<OperatorAnalyticsReferrerBucketDto> ByReferrer,
    IReadOnlyList<OperatorAnalyticsCampaignBucketDto> ByCampaign);

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

/// <summary>`18-12`: one referrer host's bucket - see <see cref="OperatorAnalyticsResponse.ByReferrer"/>.
/// </summary>
public sealed record OperatorAnalyticsReferrerBucketDto(string ReferrerHost, OperatorAnalyticsBucketDto Bucket);

/// <summary>`18-12`: one UTM campaign's bucket - see <see cref="OperatorAnalyticsResponse.ByCampaign"/>.
/// </summary>
public sealed record OperatorAnalyticsCampaignBucketDto(string UtmCampaign, OperatorAnalyticsBucketDto Bucket);
