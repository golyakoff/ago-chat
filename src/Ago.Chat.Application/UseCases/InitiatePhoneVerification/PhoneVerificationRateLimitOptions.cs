namespace Ago.Chat.Application.UseCases.InitiatePhoneVerification;

/// <summary>
/// Bound from `PhoneVerificationRateLimit:*` config keys - the `AttachmentRateLimitOptions` multi-bucket
/// shape, adapted for this item's own two-sided abuse model instead of that one's visitor/operator split.
///
/// <para><b>Why three buckets, and why <see cref="PerPhoneCapacity"/> is checked first.</b> This item's
/// own backlog Scope names two, separate threats a single bucket cannot both catch: many attempts against
/// <em>one</em> phone number (harassment - flooding a target's phone with codes it never asked for,
/// regardless of who is asking), and one visitor iterating through <em>many</em> different numbers
/// (enumeration - fishing for a number that happens to already resolve to someone else's identity, at the
/// gateway's own expense per attempt). <see cref="PerPhoneCapacity"/> answers the first, keyed on the
/// canonical <see cref="Domain.PhoneNumber"/> so every caller shares one target's own budget regardless of
/// which visitor or site is asking; <see cref="PerVisitorCapacity"/> answers the second. Checked
/// phone-bucket first - the same "cheapest-reject-first" ordering `CreateAttachmentHandler`'s own remarks
/// state, and the one this item's own send-side cost concern cares most about: a call already billed
/// against a harassed number is the more expensive mistake to let slip past a first check, since unlike
/// the visitor bucket it cannot be pre-empted by a wider site-level pattern. <see cref="PerSiteCapacity"/>
/// is checked last, the identical "a caller who was never going to pass their own bucket should not also
/// spend a share of the site's own budget finding that out" reasoning `CreateAttachmentHandler`'s own
/// remarks give for its own site bucket.</para>
///
/// <para>Defaults are a starting point, not measured or load-tested - the same caveat every
/// `*RateLimitOptions` class in this codebase carries.</para>
/// </summary>
public sealed class PhoneVerificationRateLimitOptions
{
    public const string SectionName = "PhoneVerificationRateLimit";

    public int PerPhoneCapacity { get; set; } = 3;

    public double PerPhoneRefillPerSecond { get; set; } = 3.0 / 3600;

    public int PerVisitorCapacity { get; set; } = 5;

    public double PerVisitorRefillPerSecond { get; set; } = 5.0 / 3600;

    public int PerSiteCapacity { get; set; } = 100;

    public double PerSiteRefillPerSecond { get; set; } = 100.0 / 3600;
}
