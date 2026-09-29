using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-278`: <see cref="UseCases.PurchaseChannelAddOn.PurchaseChannelAddOnHandler"/>'s own commit step -
/// the identical "one database transaction, applied only after the prorated charge already succeeded"
/// shape <see cref="IAdministratorSlotChangeApplier"/> already establishes for the analogous
/// Administrator-slot purchase.
///
/// <para><b>Unlike <see cref="IAdministratorSlotChangeApplier"/>, this applier does not mutate the row
/// the handler already loaded - it mints a brand-new <see cref="BillingSubscription"/> option row.</b>
/// A channel add-on is not a field on the base subscription the way <see cref="BillingSubscription.ExtraAdministratorsPurchased"/>
/// is; `adr/0159` models it as its own subscription (<see cref="BillingSubscription.CreateOption"/>),
/// its own period aligned to the base's, sellable and lapsable independently of it (`adr/0160`). So this
/// implementation's own transaction creates that row, immediately marks it <c>Succeeded</c>
/// (<see cref="BillingSubscription.MarkSucceeded"/> - safe here because, unlike the checkout+webhook
/// flow, the charge behind this purchase already happened synchronously, on the handler's own stored
/// payment method, before this applier is ever called), and grants the entitlement - all three, or
/// none, exactly the same all-or-nothing unit <see cref="AdministratorSlotChangeApplyRequest"/>'s own
/// implementation already commits for its own two aggregates.</para>
///
/// <para><b>The grant is written through <see cref="IModuleQuantityGrantStore"/>, in this same
/// transaction, not a network call to a module registry.</b> The identical reasoning
/// <c>SubscriptionRenewalApplier</c>'s own remarks already give for the option-renewal grant applies
/// unchanged to a first purchase: a <see cref="Domain.EnabledModule"/> grant would need a real entry
/// point, a real credential and (for a genuinely externally-routed module)
/// <see cref="IModuleRegistrationGateway.RegisterAsync"/>'s own synchronous confirmation call, and none
/// of that belongs inside a transaction that just charged a real card (CLAUDE.md rule 3's spirit).
/// This mechanism is reused, not reinvented, precisely because it is already the one this codebase
/// trusts for a billing-driven channel grant.</para>
/// </summary>
public interface IChannelAddOnPurchaseApplier
{
    Task ApplyPurchaseAsync(ChannelAddOnPurchaseApplyRequest request, CancellationToken cancellationToken);
}

/// <param name="NewOptionId">Freshly minted by the handler (<c>IIdGenerator</c>) - never derived here,
/// the identical "the caller decides the id, the applier only ever persists it" split every other
/// applier in this codebase already follows.</param>
/// <param name="SiteId">The site this option's entitlement is granted to - the same site the base
/// subscription the handler already loaded and validated belongs to.</param>
/// <param name="OptionKey">Resolved by the handler from <see cref="ChannelKind"/> through
/// <see cref="ChannelEntitlementOptionKeys.For"/> - this applier never re-derives it, matching
/// <see cref="IAdministratorSlotChangeApplier"/>'s own "the handler decides what to write, the applier
/// only writes it" split.</param>
/// <param name="YooKassaPaymentId">The stored-payment-method charge's own provider payment id
/// (<c>ChargeStoredPaymentMethodResult.Success.PaymentId</c>) - <see cref="BillingSubscription.CreateOption"/>
/// requires one, the identical field a checkout-created base row stores from its own first charge.</param>
/// <param name="PaymentMethodId">Copied from the base subscription so this option's own recurring
/// renewal charge (`26-278`'s own `ProcessSubscriptionRenewalHandler` change) has a payment method to
/// charge on file without a second lookup back to the base row.</param>
/// <param name="AlignedPeriodEnd">The base subscription's own <see cref="BillingSubscription.CurrentPeriodEnd"/>
/// at the moment of purchase - `adr/0159`'s "an option's period is copied from the base... so both
/// renew on the same date", enforced by <see cref="BillingSubscription.MarkSucceeded"/> itself refusing
/// an option with no aligned period.</param>
public sealed record ChannelAddOnPurchaseApplyRequest(
    BillingSubscriptionId NewOptionId, SiteId SiteId, ChannelKind ChannelKind, BillingOptionKey OptionKey,
    string YooKassaPaymentId, string PaymentMethodId, DateTimeOffset AlignedPeriodEnd, DateTimeOffset Now);
