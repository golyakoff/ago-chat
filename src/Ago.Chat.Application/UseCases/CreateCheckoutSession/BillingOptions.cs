namespace Ago.Chat.Application.UseCases.CreateCheckoutSession;

/// <summary>
/// `13-02`: bound from `Billing:*` - a sibling section to `Billing:YooKassa:*`
/// (`Ago.Chat.Infrastructure.YooKassa.YooKassaOptions`), kept separate because this class holds the
/// pricing *policy* (what we charge), while that one holds the *credential* (how we call ЮKassa) -
/// two different reasons to change, two different options classes, matching this codebase's existing
/// "one options class per concern" convention.
///
/// <para><b>`25-29`: <see cref="PricePerSeatRub"/> is gone, replaced by <see cref="BaseSeatPriceRub"/>
/// and <see cref="PricePerExtraSeatRub"/>.</b> `ago-business` decision `0012` (2026-09-07, read
/// directly) prices the Business tier as a flat base charge plus a marginal per-seat charge past a
/// fixed number of included seats, not `seats × one flat rate` - `Domain.SubscriptionTierBands.ComputeSeatPriceRub`
/// is the formula this pair now feeds. A single renamed property could not have expressed this: the
/// old field's whole meaning ("the one number every seat costs") stopped being true the moment `0012`
/// superseded `0008`'s flat grid, so this is a rename plus a split, not a rename alone.</para>
///
/// <para><b>Both new fields ship no default value in code, deliberately stricter than this codebase's
/// usual "hardcode a sane unmeasured default" precedent</b> (contrast `RegisterSiteRateLimitOptions`'s
/// own defaults, explicitly caveated as unmeasured starting points) - the same rule
/// <see cref="PricePerSeatRub"/> followed before this item, restated because it survives the split
/// undiminished. `CLAUDE.md`: "do not invent numbers, benchmarks, or 'typical' production figures...
/// measure or stay silent" applies with more force to a figure that charges a real card than to a
/// rate-limit bucket size a wrong guess merely inconveniences a caller with -
/// `ChatModule`'s own `.Validate().ValidateOnStart()` on this class is what turns a missing value into
/// a host that refuses to start, never a checkout that silently charges nothing or an arbitrary
/// code-level guess.</para>
///
/// <para><see cref="CheckoutReturnUrl"/> gets the identical treatment for the identical reason: a wrong
/// or missing return URL would silently strand a paying customer on ЮKassa's own hosted page after a
/// successful card charge, with no way back to the console - a failure mode CLAUDE.md's "do not invent...
/// endpoints" rule already forbids papering over with an invented placeholder domain.</para>
///
/// <para><b>Deployment note this item's own report states in full (out of this repository's reach):</b>
/// `ago-deploy`'s own k8s manifest binds `Billing__PricePerSeatRub` today. That key no longer matches
/// anything this class exposes, so a deploy of this change with no matching manifest update fails
/// `ChatModule`'s own `.ValidateOnStart()` at boot - loudly, at startup, never as a silently-zero
/// charge, but it does need the manifest updated to `Billing__BaseSeatPriceRub`/
/// `Billing__PricePerExtraSeatRub` in the same rollout, in a repository this item's own worktree has
/// no access to.</para>
/// </summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>`25-29`: `ago-business` decision `0012`'s own Business-tier base charge - what a site
    /// pays for <see cref="Domain.SubscriptionTierBands.BaseSeats"/> seats or fewer (490 ₽ at the time
    /// this item read `0012`; the number itself is configuration, not this field's own concern - see
    /// this class's own "measure or stay silent" remarks). Replaces <c>PricePerSeatRub</c>, which this
    /// item removed rather than kept alongside this field - the old name promised a single per-seat
    /// rate that no longer exists, and a config key that silently stopped being read is worse than one
    /// that fails a binder outright.</summary>
    public decimal BaseSeatPriceRub { get; set; }

    /// <summary>`25-29`: `0012`'s own marginal charge for every seat past
    /// <see cref="Domain.SubscriptionTierBands.BaseSeats"/> (200 ₽ at the time this item read `0012`) -
    /// fed into <see cref="Domain.SubscriptionTierBands.ComputeSeatPriceRub"/> alongside
    /// <see cref="BaseSeatPriceRub"/>. Never charged on its own; always added to the base.</summary>
    public decimal PricePerExtraSeatRub { get; set; }

    /// <summary>ЮKassa's own `confirmation.return_url` - where the operator's browser lands after
    /// completing (or abandoning) the hosted checkout page. Never itself proof of payment
    /// (`roadmap.md`'s "never the redirect alone") - only the webhook is.</summary>
    public string CheckoutReturnUrl { get; set; } = string.Empty;
}
