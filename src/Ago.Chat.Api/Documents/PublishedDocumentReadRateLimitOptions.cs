namespace Ago.Chat.Api.Documents;

/// <summary>
/// `24-02`: the per-IP bucket for the published surface's own read endpoints - bound from
/// <c>PublishedDocumentReadRateLimit:*</c> config keys (naming-and-structure.md's options convention),
/// the same shape <c>DemoTenantRateLimitOptions</c> already establishes for the other endpoint an
/// anonymous caller can hit with no site or subject identity to key on. Deliberately generous next to
/// that one: reading a published document is idempotent and safe to serve from cache
/// (<c>GetDocumentVersionHandler</c>'s own cache-aside), unlike minting a tenant, so the bucket only
/// has to guard against a genuine scraping loop, not an everyday page load.
/// </summary>
public sealed class PublishedDocumentReadRateLimitOptions
{
    public const string SectionName = "PublishedDocumentReadRateLimit";

    public int PerIpCapacity { get; set; } = 60;

    public double PerIpRefillPerSecond { get; set; } = 60.0 / 60; // ~60 a minute sustained
}
