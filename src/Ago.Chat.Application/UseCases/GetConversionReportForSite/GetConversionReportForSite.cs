using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversionReportForSite;

/// <summary>
/// `18-10`: the site owner's own conversion report - <paramref name="From"/> inclusive,
/// <paramref name="To"/> exclusive, either or both <see langword="null"/> meaning "let the handler
/// default the window", the identical shape `GetOperatorAnalyticsForSite` already establishes for `18-08`.
/// The console computes its own date-range presets (calendar month, previous calendar month, last 30
/// days) client-side into concrete <paramref name="From"/>/<paramref name="To"/> values before calling
/// this - see this item's own commit-prep notes for why that stays a console-only concern rather than a
/// server-side "preset" parameter.
/// </summary>
public sealed record GetConversionReportForSite(
    OperatorId RequestedBy, SiteId SiteId, DateTimeOffset? From, DateTimeOffset? To);
