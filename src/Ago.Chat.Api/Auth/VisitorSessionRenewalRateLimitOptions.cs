namespace Ago.Chat.Api.Auth;

/// <summary>
/// Bound from <c>VisitorSessionRenewalRateLimit:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).
///
/// A second options type rather than two more properties on
/// <see cref="VisitorSessionRateLimitOptions"/>, for the same reason `adr/0048` gives for renewal
/// being a second endpoint rather than a flag on the mint: the two limits are keyed on different
/// things and bound differently. The mint has no visitor identity to key on - minting one is the
/// point of the call - so it can only ever be per-site. Renewal is authenticated, so it keys on the
/// visitor, and per-visitor is strictly better there: one abusive holder of a valid token cannot
/// exhaust a bucket shared with every honest visitor on the same site. One config section covering
/// both endpoints would have to name which of the two each key belonged to anyway.
///
/// Defaults are not measured or load-tested - a starting point, the same caveat
/// <see cref="VisitorSessionRateLimitOptions"/> and `MessageSendRateLimitOptions` both carry. The
/// shape they are aimed at: a healthy visitor renews roughly once per renewal window (`adr/0048` -
/// one third of a seven-day token, so about every five days), and the burst that has to survive is a
/// visitor with several tabs open reloading, each of which renews once.
/// </summary>
public sealed class VisitorSessionRenewalRateLimitOptions
{
    public const string SectionName = "VisitorSessionRenewalRateLimit";

    public int PerVisitorCapacity { get; set; } = 5;

    public double PerVisitorRefillPerSecond { get; set; } = 5.0 / 60; // ~5 renewals/minute sustained
}
