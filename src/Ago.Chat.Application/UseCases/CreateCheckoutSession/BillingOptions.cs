namespace Ago.Chat.Application.UseCases.CreateCheckoutSession;

/// <summary>
/// `13-02`: bound from `Billing:*` - a sibling section to `Billing:YooKassa:*`
/// (`Ago.Chat.Infrastructure.YooKassa.YooKassaOptions`), kept separate because this class holds
/// deployment-level billing *configuration* (how to reach ЮKassa, where to send an operator back),
/// while pricing *policy* (what we charge) lives elsewhere now.
///
/// <para><b>`25-43`: <see cref="BaseSeatPriceRub"/>/<see cref="PricePerExtraSeatRub"/> are gone -
/// removed, not renamed a second time.</b> `25-29` split the old flat `PricePerSeatRub` into these two
/// fields; this item removes both outright, because no real Rouble figure lives in a compile-time
/// constant or `appsettings` value any more (`25-43`'s own Done-when, read literally). Every real
/// charge site now reads <see cref="Domain.SubscriptionTierBands.BaseSeatPriceKey"/>/
/// <see cref="Domain.SubscriptionTierBands.ExtraSeatPriceKey"/>'s own currently-effective version
/// through <see cref="Abstractions.IPriceCatalogRepository"/> instead - owner-published data, never a
/// value this options class binds at startup. A config key that silently stopped being read would be
/// worse than one that disappears from the class outright and fails a manifest that still sets it with
/// a clear "unknown configuration key" signal at review time, not a binder that quietly ignores
/// it.</para>
///
/// <para><b>Deployment note this item's own report states in full (out of this repository's reach):</b>
/// `ago-deploy`'s own k8s manifest binds `Billing__BaseSeatPriceRub`/`Billing__PricePerExtraSeatRub`
/// today. Both keys no longer match anything this class exposes - harmless to leave in the manifest
/// (an unbound configuration key is simply never read), but worth removing in the same rollout so a
/// future reader is not misled into thinking either still decides a real charge.</para>
/// </summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>ЮKassa's own `confirmation.return_url` - where the operator's browser lands after
    /// completing (or abandoning) the hosted checkout page. Never itself proof of payment
    /// (`roadmap.md`'s "never the redirect alone") - only the webhook is. Still ships no code default,
    /// the same "measure or stay silent" discipline `CLAUDE.md` applies to a wrong or missing return
    /// URL: a paying customer stranded with no way back to the console after a successful card charge
    /// is a failure mode `ChatModule`'s own `.ValidateOnStart()` still refuses to let a deployment
    /// reach.</summary>
    public string CheckoutReturnUrl { get; set; } = string.Empty;
}
