namespace Ago.Chat.Application.UseCases.ExportConversation;

/// <summary>
/// `24-11`: bound from <c>PersonExportRateLimit:*</c> config keys - the same `3-05` shape
/// <c>SiteExportRateLimitOptions</c> already establishes for the whole-site trigger, shared by
/// <see cref="ExportConversationHandler"/> and <c>ExportVisitorHandler</c> (one bucket per site
/// regardless of which of the two an operator calls, the same "the expense is a property of the site,
/// not of which action produced it" reasoning <see cref="SiteExportRateLimitOptions"/>'s own remarks
/// give for keying per site rather than per operator).
///
/// <para><b>Its own bucket, not a reuse of <see cref="SiteExportRateLimitOptions"/>.</b> A
/// person-scoped export is a materially smaller unit of work than a whole-site one, so sharing a
/// bucket would let a legitimate flurry of one-conversation exports exhaust the budget a tenant needs
/// for its (rarer, heavier) whole-site export, and vice versa. It also closes a disclosure-boundary
/// gap this item exists to avoid opening: without its own, deliberately tight allowance, the cheap
/// per-conversation path could be iterated over every conversation id a caller can enumerate to
/// reconstruct something close to a whole-site export without ever tripping <see cref="SiteExportRateLimitOptions"/>'s
/// own bucket.</para>
///
/// Defaults are a starting point, not measured or load-tested (CLAUDE.md: "measure or stay silent") -
/// looser than <see cref="SiteExportRateLimitOptions"/>'s per-hour trickle (a single conversation is
/// materially cheaper to build than a whole tenant's history) but still capped, for the reason above.
/// </summary>
public sealed class PersonExportRateLimitOptions
{
    public const string SectionName = "PersonExportRateLimit";

    /// <summary>Burst allowance before the refill rate takes over.</summary>
    public int PerSiteCapacity { get; set; } = 10;

    public double PerSiteRefillPerSecond { get; set; } = 1.0 / 60;
}
