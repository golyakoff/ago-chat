namespace Ago.Chat.Application.UseCases.CreateAttachment;

/// <summary>
/// `23-76`: turns a site's own tier and payment tenure into the byte ceiling
/// <see cref="CreateAttachmentHandler"/> reserves against - a pure function (no I/O, no port), the same
/// "Domain-shaped logic that only happens to need a config value" judgement
/// <c>SubscriptionTierBands</c> makes for the seat-price bands, except this one *does* need a config
/// value (<see cref="AttachmentStorageQuotaOptions"/>) and so lives in Application alongside it rather
/// than in Domain, which the dependency rule forbids from referencing an options-bound type at all.
///
/// <para><b>"Paid years" is elapsed time since the tenant's own base subscription first started,
/// nothing more.</b> <c>IBillingSubscriptionRepository.GetBaseForSiteAsync</c>'s row is created exactly
/// once per tenant and mutated in place across renewals (<c>BillingSubscription</c>'s own remarks), so
/// its <c>CreatedAt</c> is the one timestamp this codebase already has for "when did this tenant start
/// paying" - reused here rather than inventing a second ledger of "how many years has this tenant paid
/// for," which nothing in this codebase tracks and which a lapsed-then-resumed subscription would make
/// genuinely ambiguous to define. This does not distinguish a tenant paying continuously for three years
/// from one who paid for one year, lapsed back to free, and resumed two years later - both read as
/// "three years since first started." That gap is accepted, not hidden: the tier grid's own "cumulative"
/// reading is itself flagged as needing confirmation, and refining it into a real paid-time ledger is a
/// bigger change than this item's own Scope (a storage ceiling) asks for.</para>
/// </summary>
public static class SiteAttachmentQuotaPolicy
{
    public static long ComputeBudgetBytes(
        AttachmentStorageQuotaOptions options, string tier, DateTimeOffset? paidSinceUtc, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(tier) || tier == "free" || paidSinceUtc is null)
        {
            return options.FreeTierTotalBytes;
        }

        var paidYears = Math.Max(1, (int)Math.Ceiling((now - paidSinceUtc.Value).TotalDays / 365.0));
        return options.PaidTierBytesPerPaidYear * paidYears;
    }
}
