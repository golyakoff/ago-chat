using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.PurchaseChannelAddOn;

/// <summary>
/// `26-278`: the self-service half of `adr/0151`'s "…or by the system on a payment" - a tenant with an
/// already-`Succeeded` base subscription buys one <see cref="ChannelKind"/>'s own connected-channel
/// entitlement. Named against <paramref name="BaseSubscriptionId"/>, not a bare <see cref="SiteId"/> -
/// the identical "the caller names the row it is charging against" shape
/// <see cref="PurchaseAdministratorSlot.PurchaseAdministratorSlot"/> already uses, so this handler never
/// has to guess which of a site's several <see cref="BillingSubscription"/> rows (the base, or any
/// already-purchased option) is the one whose stored payment method and period this purchase aligns to.
/// </summary>
public sealed record PurchaseChannelAddOn(
    OperatorId RequestedBy, SiteId SiteId, BillingSubscriptionId BaseSubscriptionId, ChannelKind ChannelKind);

/// <summary>`ProratedAmountRub` is what was actually charged - the remainder of the base subscription's
/// own current period, at `channel-addon`'s currently-effective price, the identical proration
/// `PurchaseAdministratorSlotResult` already reports for the analogous Administrator-slot purchase.
/// <see cref="OptionSubscriptionId"/> is the new, own <see cref="BillingSubscription"/> row this purchase
/// minted (<see cref="BillingSubscription.CreateOption"/>) - a caller that needs to cancel or inspect this
/// specific channel's own billing row later has no other way to learn its id.</summary>
public sealed record PurchaseChannelAddOnResult(
    decimal ProratedAmountRub, ChannelKind ChannelKind, BillingSubscriptionId OptionSubscriptionId);
