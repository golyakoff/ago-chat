using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.PreviewBillingPurchase;

/// <summary>`26-299`: which dimension a preview is asking about - the identical three purchasable things
/// this codebase's own instant-charge handlers already cover
/// (<c>ChangeSubscriptionSeatsHandler</c>/<c>PurchaseAdministratorSlotHandler</c>/<c>PurchaseChannelAddOnHandler</c>),
/// named here so one read-only query can answer for any of them rather than three separate endpoints.</summary>
public enum BillingPurchaseKind
{
    Seats,
    Administrators,
    Channel,
}

/// <summary>
/// `26-299`: `.../billing/subscriptions/{subscriptionId}/purchase-preview`'s own query - a read, never a
/// charge, that computes the exact number an actual purchase would charge so the console can show
/// «Докупить за ₽X» honestly before the operator commits to anything. <see cref="Kind"/> decides which of
/// <see cref="RequestedSeats"/>/<see cref="RequestedExtraAdministrators"/>/<see cref="ChannelKind"/> the
/// handler reads; the other two are ignored for that kind rather than validated as absent, the same
/// "the caller sends what is relevant to the one thing being asked" shape a discriminated request body
/// naturally has once flattened onto one wire shape.
/// </summary>
public sealed record PreviewBillingPurchase(
    OperatorId RequestedBy,
    SiteId SiteId,
    BillingSubscriptionId SubscriptionId,
    BillingPurchaseKind Kind,
    int? RequestedSeats,
    int? RequestedExtraAdministrators,
    ChannelKind? ChannelKind);

/// <param name="ChargedNowRub">What an actual purchase of this shape would charge immediately, at this
/// type's own <see cref="Domain.BillingProration"/> floor rule - identical arithmetic to whatever the real
/// purchase handler for <see cref="PreviewBillingPurchase.Kind"/> would compute at this exact moment, so
/// "preview" and "charge" can never honestly disagree.</param>
/// <param name="ThenRecurringRub">How much this purchase adds to the subscription's own recurring monthly
/// charge from the next period onward - the marginal addition, not the whole subscription's new total
/// (the console already has that total from <c>GetBillingStatus</c>'s own <c>NextChargeRub</c>).</param>
/// <param name="IncludedUntil">The base subscription's own current period end - what an actual purchase
/// would align a brand-new option's own period to, or simply the date <see cref="ChargedNowRub"/> buys
/// access through for a seat/Administrator increase.</param>
public sealed record BillingPurchasePreviewResult(decimal ChargedNowRub, decimal ThenRecurringRub, DateTimeOffset IncludedUntil);
