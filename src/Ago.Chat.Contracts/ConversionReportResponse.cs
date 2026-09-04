namespace Ago.Chat.Contracts;

/// <summary>
/// `18-10`: `GET /api/v1/conversations/conversion-report`'s response body. <see cref="From"/>/
/// <see cref="To"/> are the bound this report actually used - always present, the same "the bound is
/// visible, not silent" shape `OperatorAnalyticsResponse.From`/`To` already establishes for `18-08`.
///
/// <b>This number is not a verified sale count - the console must say so wherever it renders
/// <see cref="Overall"/>/<see cref="ByOperator"/>.</b> See <c>Ago.Chat.Domain.ConversationOutcome</c>'s
/// own remarks (`ago-chat`) for the full reasoning this wire contract carries no field for: every count
/// here comes from what an operator chose to record, never from a verified order or payment.
///
/// <b>`23-16`: <see cref="PreviousOverall"/> is the immediately preceding window of equal length</b> -
/// see <c>Ago.Chat.Application.Abstractions.PrecedingPeriod</c> for exactly how it is computed and why
/// server-side, once, rather than by the console calling this endpoint twice. <see cref="PreviousFrom"/>/
/// <see cref="PreviousTo"/> are that window's own bound, echoed back the same "never silent" way
/// <see cref="From"/>/<see cref="To"/> already are.
/// </summary>
public sealed record ConversionReportResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    ConversionBucketDto Overall,
    DateTimeOffset PreviousFrom,
    DateTimeOffset PreviousTo,
    ConversionBucketDto PreviousOverall,
    IReadOnlyList<ConversionOperatorBucketDto> ByOperator);

/// <param name="ConvertedCount">Conversations recorded as <c>Converted</c>.</param>
/// <param name="NotConvertedCount">Conversations recorded as <c>NotConverted</c>.</param>
/// <param name="FollowUpNeededCount">Conversations recorded as <c>FollowUpNeeded</c> - excluded from
/// both halves of <paramref name="ConversionRate"/>'s own fraction.</param>
/// <param name="UnsetCount">Conversations nobody has recorded an outcome for at all - excluded from
/// <paramref name="ConversionRate"/>'s denominator entirely. The console should render this prominently
/// next to the rate, not bury it, since it is the number that says how much of the window the rate is
/// actually based on.</param>
/// <param name="RecordedCount"><paramref name="ConvertedCount"/> + <paramref name="NotConvertedCount"/> -
/// the exact denominator <paramref name="ConversionRate"/> is computed over.</param>
/// <param name="ConversionRate"><see langword="null"/> when <paramref name="RecordedCount"/> is zero -
/// never zero itself.</param>
public sealed record ConversionBucketDto(
    long ConvertedCount, long NotConvertedCount, long FollowUpNeededCount, long UnsetCount,
    long RecordedCount, double? ConversionRate);

/// <summary>`18-10`'s own per-operator breakdown. <see cref="OperatorId"/> is the conversation's own
/// <c>operator_id</c> column (currently/last-assigned) - see <c>IConversionReportReadStore</c>'s own
/// remarks (`ago-chat`) for why this needs none of `18-09`'s first-reply-attribution ambiguity.
///
/// <para>`23-02`: <see cref="OperatorName"/> is that operator's own display name - <see langword="null"/>
/// for a row that predates the column, the same fallback-to-id shape
/// `OperatorAnalyticsOperatorBucketDto.OperatorName` already establishes.</para></summary>
public sealed record ConversionOperatorBucketDto(Guid OperatorId, ConversionBucketDto Bucket, string? OperatorName = null);
