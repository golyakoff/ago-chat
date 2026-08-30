namespace Ago.Chat.Domain;

/// <summary>
/// `18-12`: where a conversation's own visitor actually came from - the referring page's host, and
/// whichever of the three UTM query parameters the landing page's own URL carried. Captured once, at
/// the moment <see cref="Conversation.Start"/> runs, and never revisited afterward - the same
/// "captured once, at the moment that matters, never revisited" shape <see cref="ChannelIdentity.FirstSeenAt"/>
/// already established elsewhere in this codebase, restated here for the same reason: a
/// <em>returning</em> visitor can arrive by a different route on every visit, so the interesting fact is
/// where <em>this</em> conversation's visitor came from right now, not where that browser's very
/// first-ever page view came from (which would belong on <see cref="Visitor"/>, and does not -
/// `docs/backlog/18-12-visitor-traffic-source-report.md`'s own "Why this field goes on Conversation, not
/// Visitor" explains the rejected alternative).
///
/// <para><b>Every field is exactly what the widget read from the browser - unverified.</b> A referrer
/// header is client-supplied and can be spoofed, stripped, or simply empty (a direct visit, a
/// privacy-blocking browser setting, or a referrer-stripping browser - a common, expected case, not an
/// error). Nothing here confirms any of it against a second source, and no caller should ever present it
/// as more than "what the browser reported" - the same honesty discipline `18-10`'s own
/// operator-reported-outcome framing already holds itself to, for a different underlying reason (there
/// it is a human's self-report; here it is an unverifiable client-supplied header).</para>
///
/// <para><b>Nothing here is collapsed into a coarse bucket.</b> A shop running a named ad campaign needs
/// to see that campaign's own name - <see cref="UtmCampaign"/> - not just "external referral". Bucketing
/// at capture time would throw away the one thing that makes this report worth building over `18-08`'s
/// existing per-channel counts (the backlog item's own Scope section). The report side groups this data
/// at query time instead; storage keeps everything the browser handed over.</para>
///
/// <para><b>Bounded, not rejected.</b> Each field is truncated to <see cref="MaxLength"/> rather than
/// throwing the way <see cref="MessageBody"/> and <see cref="ExternalChannelAddress"/> throw on an
/// over-length value - deliberately different from both. Those two wrap something a person typed, where
/// refusing an oversized value is a meaningful signal back to its author. A referrer header or a query
/// string is not authored by anyone at the keyboard; the widget captures it automatically, with no user
/// interaction to reject on behalf of, and failing an entire conversation start over a malformed or
/// adversarial URL would be strictly worse than just bounding it. Truncation is the same "an adversarial
/// or malformed URL should not become an unbounded write" protection the backlog item asks for, applied
/// in the direction that keeps the conversation startable.</para>
/// </summary>
public sealed record TrafficSource
{
    // Generous enough for a real UTM campaign name or a referrer host with a long subdomain chain,
    // small enough that four of these on one row is never the reason an insert is slow - the same
    // "generous but bounded, no product requirement pins the exact number" reasoning MessageBody's own
    // MaxLength remarks give.
    public const int MaxLength = 512;

    public string? ReferrerHost { get; }

    public string? UtmSource { get; }

    public string? UtmMedium { get; }

    public string? UtmCampaign { get; }

    public TrafficSource(string? referrerHost, string? utmSource, string? utmMedium, string? utmCampaign)
    {
        ReferrerHost = Bound(referrerHost);
        UtmSource = Bound(utmSource);
        UtmMedium = Bound(utmMedium);
        UtmCampaign = Bound(utmCampaign);
    }

    /// <summary>
    /// True when the browser reported nothing at all - the common case (a direct visit, or one where
    /// the referrer was stripped and no campaign link was used). <see cref="Conversation.Start"/> checks
    /// this and stores <see langword="null"/> rather than an all-null value object, so "no source
    /// captured" and "a conversation that predates this item" read identically at rest, with no reader
    /// needing to know the difference.
    /// </summary>
    public bool IsEmpty =>
        ReferrerHost is null && UtmSource is null && UtmMedium is null && UtmCampaign is null;

    /// <summary>Empty/whitespace collapses to <see langword="null"/> rather than an empty string, so a
    /// reader (this type's own <see cref="IsEmpty"/>, and the report's "Direct"/no-campaign fallback)
    /// can tell "genuinely absent" apart from an accidental empty value using one check.</summary>
    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > MaxLength ? trimmed[..MaxLength] : trimmed;
    }
}
