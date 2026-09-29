namespace Ago.Chat.Domain;

/// <summary>
/// `25-101`: the flat, per-channel-category add-on price - one Rouble figure for "a connected channel"
/// as a whole, never one per <see cref="ChannelKind"/>. The author's own decision
/// (`docs/backlog/25-101-*.md`): every channel kind (Telegram, MAX, WhatsApp, ...) shares this
/// identical price, matching `ago-business/0008`'s own Telegram/MAX figures already being identical,
/// and matching <see cref="BillingSubscription"/>'s own remarks already modeling a purchased option as
/// flat-priced - no seat count, no tier band.
///
/// <para><b>Its own file, not folded into <see cref="SubscriptionTierBands"/> or
/// <see cref="DownloadOveragePricing"/>.</b> The identical split those two types' own remarks already
/// draw between "the seat ladder" and "a per-gigabyte meter" applies again here, for a third shape:
/// no band, no per-unit rate, just one number.</para>
///
/// <para><b>The price itself is not here</b>, the identical split <see cref="DownloadOveragePricing"/>'s
/// own remarks draw for its own key: <see cref="ChannelAddOnKey"/> is a <see cref="PriceKey"/>,
/// resolved through <c>IPriceCatalogRepository</c> by whatever reads it.</para>
///
/// <para><b>Not <c>ChannelEntitlementOptionKeys</c>'s own <c>"channel-" + kind</c> naming.</b> That
/// type answers a different question - which <see cref="Application.Abstractions.IBillingOptionEntitlementProvider"/>
/// entry a specific <see cref="ChannelKind"/> turns on - on a deliberately per-kind axis (`adr/0151`:
/// "the deployment declares what an option turns on; it never declares what it costs"). This key is
/// the opposite axis on purpose: one price, independent of which kind, so it is named for the category
/// ("an add-on"), never for any one channel - naming it e.g. `channel-telegram` here would read as
/// though a second, per-kind price list were about to grow up beside the entitlement one, which is
/// exactly the shape this item's own design rejects.</para>
/// </summary>
public static class ChannelAddOnPricing
{
    /// <summary>`25-101`'s own priced resource, registered in <see cref="PricedResourceKeys"/> the
    /// same change that added this key.</summary>
    public static readonly PriceKey ChannelAddOnKey = new("channel-addon");

    /// <summary>
    /// `26-278`: the option-key -&gt; price-key resolution `ProcessSubscriptionRenewalHandler`'s own
    /// option branch needs, so it can charge a channel option's recurring fee instead of throwing on
    /// every option row. Every <paramref name="optionKey"/> spelled <c>"channel-" + kind</c>
    /// (<see cref="ChannelEntitlementOptionKeys.For"/>'s own naming convention) resolves to this flat
    /// <see cref="ChannelAddOnKey"/> - one price, independent of which kind, this type's own remarks
    /// already state. <see langword="null"/> for every other option key (in particular, an AI option -
    /// <c>ai-*</c>) - `ago-business` decision `0012` deliberately publishes no price for AI usage yet
    /// ("нет, и поэтому цены не публикуются"), and a <see langword="null"/> here is what keeps
    /// <see cref="Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewalHandler"/>
    /// throwing for that case exactly as it already did, unchanged.
    ///
    /// <para><b>Domain, not Application.</b> "Which price key a `channel-*` option resolves to" is a
    /// stable fact about this codebase's own vocabulary - identical in every deployment - not a
    /// deployment-configured mapping (contrast <see cref="Application.Abstractions.IBillingOptionEntitlementProvider"/>,
    /// genuinely opaque to this assembly and resolved only by what a deployment declares). The
    /// alternative, a <see langword="switch"/> living in the renewal handler itself, would scatter this
    /// pricing policy across layers instead of keeping it beside the vocabulary it is a fact about - the
    /// identical placement judgement <see cref="ChannelEntitlementOptionKeys"/>'s own remarks already
    /// make for the sibling option-key -&gt; module-key question.</para>
    ///
    /// <para>A prefix check, not a per-<see cref="ChannelKind"/> <see langword="switch"/> the way
    /// <see cref="ChannelEntitlementOptionKeys.For"/> is - that type enumerates every kind because a
    /// missing arm there must fail to compile the moment a new kind is added unpriced-for-entitlement;
    /// here, by contrast, every <c>channel-*</c> key (present or future) is priced identically on
    /// purpose, so a new <see cref="ChannelKind"/> needs no matching edit to this method at all - the
    /// one property a flat, kind-independent price is supposed to have.</para>
    /// </summary>
    public static PriceKey? PriceKeyFor(BillingOptionKey optionKey) =>
        optionKey.Value.StartsWith("channel-", StringComparison.Ordinal) ? ChannelAddOnKey : null;
}
