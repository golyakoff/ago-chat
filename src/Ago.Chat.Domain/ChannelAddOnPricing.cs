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
/// no band, no per-unit rate, just one number. Unlike <see cref="DownloadOveragePricing"/>, this file
/// carries no arithmetic helper at all - there is nothing to compute, because reading and charging
/// against this key belongs to whichever tenant-facing purchase flow this item's own Scope
/// deliberately excludes ("out of scope, deliberately: anything that reads this price to charge a
/// tenant or grant a channel entitlement").</para>
///
/// <para><b>The price itself is not here</b>, the identical split <see cref="DownloadOveragePricing"/>'s
/// own remarks draw for its own key: <see cref="ChannelAddOnKey"/> is a <see cref="PriceKey"/>,
/// resolved through <c>IPriceCatalogRepository</c> by whatever eventually reads it - nothing does yet,
/// deliberately, since this item builds only the half the platform owner needs first in the causal
/// chain: the key existing for the owner's own publish screen to show and set a real figure against,
/// before anyone can buy anything at it.</para>
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
    /// same change that adds this key - see this type's own remarks for why nothing reads it yet.</summary>
    public static readonly PriceKey ChannelAddOnKey = new("channel-addon");
}
