namespace Ago.Chat.Application.UseCases.RequestSiteExport;

/// <summary>
/// Bound from <c>SiteExportRateLimit:*</c> config keys - the same `3-05` shape every other rate-limit
/// options class in this codebase uses (<c>RegisterSiteRateLimitOptions</c>,
/// <c>AttachmentRateLimitOptions</c>). One bucket, keyed per site rather than per operator - the
/// backlog item's own words: "export is the cheapest way to make this deployment do expensive work on
/// demand", and the expense (streaming a tenant's full history to a temp file, then to object storage)
/// is a property of the site being exported, not of which operator happened to click the button. A
/// site with several admins sharing one budget is the deliberate choice: two admins racing to trigger
/// exports should not double the Worker's load just because they hold different operator ids.
///
/// Defaults are a starting point, not measured or load-tested (CLAUDE.md: "measure or stay silent") -
/// deliberately conservative, tighter than <c>RegisterSiteRateLimitOptions</c>'s per-IP bucket, because
/// a single export is materially more expensive than a single site registration.
/// </summary>
public sealed class SiteExportRateLimitOptions
{
    public const string SectionName = "SiteExportRateLimit";

    /// <summary>Burst allowance before the refill rate takes over - enough for a tenant retrying a
    /// failed download link a couple of times without waiting an hour.</summary>
    public int PerSiteCapacity { get; set; } = 3;

    public double PerSiteRefillPerSecond { get; set; } = 1.0 / 3600;
}
