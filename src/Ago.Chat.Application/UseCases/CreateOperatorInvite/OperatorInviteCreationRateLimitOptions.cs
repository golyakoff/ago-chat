namespace Ago.Chat.Application.UseCases.CreateOperatorInvite;

/// <summary>
/// `25-73`: "five per site per day, refused past that... the number itself in `Ago.Chat`'s own global
/// config (not buried in a handler) so it is one line to change" (this item's own point 5). Bound from
/// `OperatorInviteCreationRateLimit:*` config keys - the same `3-05`/`SiteExportRateLimitOptions` shape
/// every rate-limit options class in this codebase already uses, and the identical per-*site* keying
/// <see cref="Application.UseCases.RequestSiteExport.SiteExportRateLimitOptions"/> already establishes
/// for the analogous reason: sending invite mail is a real message to an arbitrary third party's inbox
/// from this deployment's own shared Postfix (a reputation surface that did not exist before this item),
/// and the budget is a fact about the site doing the inviting, not about which operator happens to click
/// the button.
///
/// <para>A token bucket, not a calendar-day counter - the same "reuse `IRateLimiter`, no new mechanism"
/// choice every other rate limit in this codebase already makes (no existing per-site daily-counter
/// pattern was found to reuse instead). <see cref="PerSiteCapacity"/> is the burst (five invites sent in
/// a row still succeed); <see cref="PerSiteRefillPerSecond"/> replenishes one token per day after that,
/// so "five a day, sustained" is what this bucket actually enforces - a rolling window, not a
/// midnight-UTC reset, which still refuses a sixth invite within the same day exactly as this item's own
/// Done-when requires.</para>
///
/// Defaults are a starting point, not measured or load-tested (`CLAUDE.md`: "measure or stay silent").
/// </summary>
public sealed class OperatorInviteCreationRateLimitOptions
{
    public const string SectionName = "OperatorInviteCreationRateLimit";

    public int PerSiteCapacity { get; set; } = 5;

    public double PerSiteRefillPerSecond { get; set; } = 1.0 / 86400; // one more invite per day, sustained
}
