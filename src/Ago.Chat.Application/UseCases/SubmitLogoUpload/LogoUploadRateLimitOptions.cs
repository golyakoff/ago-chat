namespace Ago.Chat.Application.UseCases.SubmitLogoUpload;

/// <summary>
/// `25-160`: "5 logo replacements per tenant per day... via the existing `IRateLimiter` port... a
/// fourth, single-bucket case, not a new mechanism" (this item's own Design decisions). The identical
/// token-bucket-as-daily-budget shape <c>OperatorInviteCreationRateLimitOptions</c> already establishes
/// for the identical "N per site per day" requirement: a burst capacity of 5 and a refill rate of one
/// token per day, so five uploads in a row still succeed and a sixth within the same rolling day does
/// not - a rolling window, not a midnight-UTC reset, matching this item's own Done-when literally
/// ("more than 5 upload attempts for one site in a day are refused").
///
/// Defaults are a starting point, not measured or load-tested - the same caveat every
/// `*RateLimitOptions` class in this codebase carries.
/// </summary>
public sealed class LogoUploadRateLimitOptions
{
    public const string SectionName = "LogoUploadRateLimit";

    public int PerSiteCapacity { get; set; } = 5;

    public double PerSiteRefillPerSecond { get; set; } = 1.0 / 86400;
}
