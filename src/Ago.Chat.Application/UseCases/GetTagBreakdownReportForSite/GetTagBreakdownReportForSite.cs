using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetTagBreakdownReportForSite;

/// <summary>
/// `18-11`: the site owner's own tag breakdown report - <paramref name="From"/> inclusive,
/// <paramref name="To"/> exclusive, either or both <see langword="null"/> meaning "let the handler
/// default the window", the identical shape `GetOperatorAnalyticsForSite`/`GetConversionReportForSite`
/// already establish. The console resolves its own date-range presets client-side into concrete
/// <paramref name="From"/>/<paramref name="To"/> values before calling this, the same "no server-side
/// preset concept" decision `GetConversionReportForSite`'s own commit-prep notes already record.
/// </summary>
public sealed record GetTagBreakdownReportForSite(
    OperatorId RequestedBy, SiteId SiteId, DateTimeOffset? From, DateTimeOffset? To);
