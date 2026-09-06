namespace Ago.Chat.Api.WidgetActivity;

/// <summary>
/// `23-07`: the beacon's own per-IP bucket - bound from `WidgetActivityBeaconRateLimit:*` config keys
/// (naming-and-structure.md's options convention). Per IP, not per site the way
/// <see cref="Auth.VisitorSessionRateLimitOptions"/> is - the backlog item's own Scope states this
/// explicitly ("rate-limited per IP through IRateLimiter"), the same shape
/// <see cref="Documents.PublishedDocumentReadRateLimitOptions"/> already takes for its own anonymous
/// caller with no site or subject identity to key a per-site bucket on cheaply (a beacon carries a
/// public key, but keying on it would let one visitor's flood exhaust every other visitor's own
/// bucket on a busy shared tenant, which is the opposite of what this limiter exists to stop).
///
/// <para><b>Deliberately generous</b> - "the highest-volume public endpoint in the product" (the
/// item's own words) is one beacon per mount plus at most one per session for an open, so an ordinary
/// visitor never approaches this bucket; it exists to blunt a scripted flood, not to throttle real
/// traffic. Not measured or load-tested - the same caveat every other `*RateLimitOptions` default in
/// this codebase carries.</para>
/// </summary>
public sealed class WidgetActivityBeaconRateLimitOptions
{
    public const string SectionName = "WidgetActivityBeaconRateLimit";

    public int PerIpCapacity { get; set; } = 120;

    public double PerIpRefillPerSecond { get; set; } = 120.0 / 60; // ~120 a minute sustained
}
