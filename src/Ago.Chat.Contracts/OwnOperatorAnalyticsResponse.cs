namespace Ago.Chat.Contracts;

/// <summary>
/// `23-18`: `GET /api/v1/conversations/analytics/me`'s response body - one operator's own row of
/// `18-08`/`23-17`'s tenant report (<see cref="Bucket"/>/<see cref="Load"/>) and their own row of
/// `18-10`'s conversion report (<see cref="Conversion"/>), for the caller's own operator id only.
///
/// <b>There is no operator id anywhere on this shape, on purpose.</b> The tenant-wide reports' own
/// per-operator DTOs (<see cref="OperatorAnalyticsOperatorBucketDto"/>, <see cref="ConversionOperatorBucketDto"/>)
/// carry one because they list many operators; this response is always about exactly one, the caller,
/// so naming it again would only invite a client to compare it against something - the one comparison
/// this screen must never offer (`docs/design/flows.md` 2.4's forbidden leaderboard).
///
/// <para><see cref="From"/>/<see cref="To"/> are the bound this report actually used - always present,
/// the same "the bound is visible, not silent" convention <c>OperatorAnalyticsResponse.From</c>/<c>To</c>
/// already establishes.</para>
/// </summary>
/// <param name="Bucket">Zero-filled (never <see langword="null"/>) when this operator attributed no
/// conversation in the window - the operator's own screen always renders something rather than
/// reading as broken on a slow day (<c>GetOwnAnalyticsForOperatorHandler</c>'s own remarks).</param>
/// <param name="Load"><see langword="null"/> when this operator held no assignment interval starting in
/// the window - a real "no data", the identical distinction
/// <see cref="OperatorAnalyticsOperatorBucketDto.Load"/> already documents. When present, the two counts
/// - <see cref="OperatorLoadSummaryDto.StandardIntervals"/>/<see cref="OperatorLoadSummaryDto.AdditionalIntervals"/>
/// - stay two counts here exactly as they do on the tenant's report: never combined into one score.</param>
/// <param name="Conversion"><see langword="null"/> when this operator has no conversation with a
/// recorded outcome in the window - the identical "no manufactured row" rule
/// <c>Ago.Chat.Application.Abstractions.ConversionReportResult</c>'s own <c>ByOperator</c> already
/// holds, applied to one row instead of a list.</param>
public sealed record OwnOperatorAnalyticsResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    OperatorAnalyticsBucketDto Bucket,
    OperatorLoadSummaryDto? Load,
    ConversionBucketDto? Conversion);
