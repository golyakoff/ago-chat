namespace Ago.Chat.Application.UseCases.RecordVisitorConsent;

/// <summary>
/// `24-05`: rate limiting for <c>RecordVisitorConsentHandler</c> - the identical two-bucket, "per
/// visitor then per site, cheapest-reject-first" shape <c>ContactDetailRateLimitOptions</c> already
/// establishes for the visitor's own contact-detail write, kept as its own options type rather than a
/// reuse of that one: a consent acceptance and a contact detail are different resources with
/// independently tunable budgets, the same "one options type per feature" convention every other
/// `*RateLimitOptions` class in this codebase already follows.
///
/// <para>Defaults are a starting point, not measured or load-tested - the same caveat every
/// `*RateLimitOptions` class in this codebase carries.</para>
/// </summary>
public sealed class ConsentRateLimitOptions
{
    public const string SectionName = "ConsentRateLimit";

    public int PerVisitorCapacity { get; set; } = 10;

    public double PerVisitorRefillPerSecond { get; set; } = 10.0 / 3600;

    public int PerSiteCapacity { get; set; } = 200;

    public double PerSiteRefillPerSecond { get; set; } = 200.0 / 3600;
}
