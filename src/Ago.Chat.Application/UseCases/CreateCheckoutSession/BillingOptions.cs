namespace Ago.Chat.Application.UseCases.CreateCheckoutSession;

/// <summary>
/// `13-02`: bound from `Billing:*` - a sibling section to `Billing:YooKassa:*`
/// (`Ago.Chat.Infrastructure.YooKassa.YooKassaOptions`), kept separate because this class holds the
/// pricing *policy* (what we charge), while that one holds the *credential* (how we call ЮKassa) -
/// two different reasons to change, two different options classes, matching this codebase's existing
/// "one options class per concern" convention.
///
/// <para><b><see cref="PricePerSeatRub"/> ships no default value in code, deliberately stricter than
/// this codebase's usual "hardcode a sane unmeasured default" precedent</b> (contrast
/// `RegisterSiteRateLimitOptions`'s own defaults, explicitly caveated as unmeasured starting points).
/// `CLAUDE.md`: "do not invent numbers, benchmarks, or 'typical' production figures... measure or stay
/// silent" applies with more force to a figure that charges a real card than to a rate-limit bucket
/// size a wrong guess merely inconveniences a caller with - `ChatModule`'s own `.Validate().ValidateOnStart()`
/// on this class is what turns a missing value into a host that refuses to start, never a checkout
/// that silently charges nothing or an arbitrary code-level guess.</para>
///
/// <para><see cref="CheckoutReturnUrl"/> gets the identical treatment for the identical reason: a wrong
/// or missing return URL would silently strand a paying customer on ЮKassa's own hosted page after a
/// successful card charge, with no way back to the console - a failure mode CLAUDE.md's "do not invent...
/// endpoints" rule already forbids papering over with an invented placeholder domain.</para>
/// </summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>Total charge = <c>seats_requested × PricePerSeatRub</c> - this item's own deliberately
    /// minimal, flat-linear pricing mechanism (no per-band discount; <see cref="Domain.SubscriptionTierBands"/>'s
    /// own remarks on why not).</summary>
    public decimal PricePerSeatRub { get; set; }

    /// <summary>ЮKassa's own `confirmation.return_url` - where the operator's browser lands after
    /// completing (or abandoning) the hosted checkout page. Never itself proof of payment
    /// (`roadmap.md`'s "never the redirect alone") - only the webhook is.</summary>
    public string CheckoutReturnUrl { get; set; } = string.Empty;
}
