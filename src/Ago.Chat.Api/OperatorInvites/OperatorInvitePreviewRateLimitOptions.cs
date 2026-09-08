namespace Ago.Chat.Api.OperatorInvites;

/// <summary>
/// `23-70`: the per-IP bucket for the invite landing page's own anonymous read - bound from
/// `OperatorInvitePreviewRateLimit:*` config keys (`naming-and-structure.md`'s options convention), the
/// same shape <c>PublishedDocumentReadRateLimitOptions</c> already establishes for an anonymous caller
/// with no site or subject identity to key on instead. Tighter than that one: a document read is pure
/// reference-data scraping risk, while this route's own input is a bearer-style secret
/// (`OperatorInviteCodeGenerator`'s own 256-bit CSPRNG) - the bucket exists to blunt an automated
/// guessing loop, not ordinary page loads, so it is sized for "a person opening the one link they were
/// sent, maybe twice" rather than "a script trying codes".
/// </summary>
public sealed class OperatorInvitePreviewRateLimitOptions
{
    public const string SectionName = "OperatorInvitePreviewRateLimit";

    public int PerIpCapacity { get; set; } = 20;

    public double PerIpRefillPerSecond { get; set; } = 20.0 / 60; // ~20 a minute sustained
}
