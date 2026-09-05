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
/// still credits whoever answered it, never whoever it was transferred to.
///
/// <para>`23-02`: <see cref="OperatorName"/> is that operator's own display name -
/// <see langword="null"/> for a row that predates the column (or an operator `MintDemoTenantHandler`
/// minted, which carries none by design). The console renders this with the id as its own fallback,
/// never the other way round - <see cref="OperatorId"/> stays present on every row regardless, so a
/// caller never loses the ability to tell two same-named operators apart.</para>
///
/// <para>`23-17`: <see cref="Load"/> is a second, independent view of this same operator - "held", from
/// `conversation_assignments`, rather than "attributed to", from message authorship
/// (<see cref="Bucket"/>'s own definition, `IOperatorAnalyticsReadStore`'s remarks). <see langword="null"/>
/// when the operator has no assignment interval starting in the window at all - a row that predates
/// `23-03`'s own table, or (this window's own edge case) an operator who holds a conversation started
/// and answered entirely before the window but never took on anything new inside it. That is a real
/// "no data", not a zero: see <see cref="OperatorLoadSummaryDto"/>'s own remarks for why it is shown
/// differently from an operator whose load report says zero additional.</para>
/// </summary>
public sealed record OperatorAnalyticsOperatorBucketDto(
    Guid OperatorId, OperatorAnalyticsBucketDto Bucket, string? OperatorName = null, OperatorLoadSummaryDto? Load = null);

/// <summary>`23-17`: one operator's load summary - see `Ago.Chat.Application.Abstractions.IOperatorLoadReportReadStore`
/// for the full "why this shape" statement. A distinct type from <see cref="OperatorAnalyticsBucketDto"/>
/// on purpose: the two answer different questions over different windows of the same underlying data
/// (conversations this operator was credited with answering, versus conversations this operator's own
/// assignment intervals say they held), and folding load counts into the existing bucket shape would
/// make a reader guess which of the two "conversation count" on a combined record actually meant.
/// </summary>
/// <param name="ConversationsHeld">Distinct conversations with at least one interval starting in the
/// window - a conversation transferred away and back to this same operator counts once here.</param>
/// <param name="IntervalsHeld">Every assignment interval, so the same transferred-away-and-back
/// conversation counts twice here. Never less than <see cref="ConversationsHeld"/>; equal to it exactly
/// when nothing in the window was ever held twice by the same operator.</param>
/// <param name="StandardIntervals">Intervals where this operator's own concurrent load, counting the
/// interval itself, did not exceed their capacity when it started.</param>
/// <param name="AdditionalIntervals">Intervals where it did - `docs/design/decisions.md` §2's naming
/// amendment: computed from interval overlap against capacity, never a stored flag, and never labelled
/// "forced" anywhere a person reads it. <see cref="StandardIntervals"/> + <see cref="AdditionalIntervals"/>
/// == <see cref="IntervalsHeld"/> always - an operator who never exceeded capacity in the window shows
/// <c>0</c> here, exactly as real a fact as a non-zero count, never rendered as a criticism.</param>
/// <param name="ByLoad">Response time bucketed by the operator's own concurrent load at the moment each
/// reply was owed - ordered by bucket ascending, a listing, never a ranking (this report never sorts
/// operators against each other).</param>
public sealed record OperatorLoadSummaryDto(
    long ConversationsHeld,
    long IntervalsHeld,
    long StandardIntervals,
    long AdditionalIntervals,
    IReadOnlyList<OperatorLoadBucketEntryDto> ByLoad);

/// <param name="BucketLabel">E.g. <c>"1"</c>, <c>"2-3"</c>, <c>"9+"</c> -
/// `Ago.Chat.Application.Abstractions.OperatorLoadBuckets.Label`'s own output for
/// `Analytics:LoadBucketUpperBounds`'s configured boundaries. A display string, not a value to parse
/// back apart.</param>
/// <param name="IntervalCount">Intervals that started at this bucket's own load.</param>
/// <param name="ReplyCount">How many of those ever saw a reply from the operator who held them - the
/// denominator <see cref="AverageFirstReplySeconds"/> is averaged over, always shown alongside it
/// rather than left implicit.</param>
/// <param name="AverageFirstReplySeconds"><see langword="null"/> when <see cref="ReplyCount"/> is zero -
/// never zero itself.</param>
public sealed record OperatorLoadBucketEntryDto(
    string BucketLabel, long IntervalCount, long ReplyCount, double? AverageFirstReplySeconds);

/// <summary>`18-12`: one referrer host's bucket - see <see cref="OperatorAnalyticsResponse.ByReferrer"/>.
/// </summary>
public sealed record OperatorAnalyticsReferrerBucketDto(string ReferrerHost, OperatorAnalyticsBucketDto Bucket);

/// <summary>`18-12`: one UTM campaign's bucket - see <see cref="OperatorAnalyticsResponse.ByCampaign"/>.
/// </summary>
public sealed record OperatorAnalyticsCampaignBucketDto(string UtmCampaign, OperatorAnalyticsBucketDto Bucket);
