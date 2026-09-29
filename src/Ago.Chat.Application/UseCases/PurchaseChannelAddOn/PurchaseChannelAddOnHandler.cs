using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.PurchaseChannelAddOn;

/// <summary>
/// `26-278`: `PurchaseAdministratorSlotHandler`'s own prorated, charge-then-apply shape
/// (`decisions/0006`), restated for a channel add-on rather than a flat Administrator-slot increase -
/// the two differ only in what gets prorated and what the successful charge creates.
///
/// <para><b>The price READ lives here, in the Application handler - never in Domain.</b>
/// <see cref="ChannelAddOnPricing.PriceKeyFor"/> only says which <see cref="PriceKey"/> a channel option
/// resolves to; fetching the actual, currently-effective Rouble figure is <see cref="IPriceCatalogRepository"/>'s
/// job, a port declared in <c>Application/Abstractions</c> (CLAUDE.md rule 2) - Domain references
/// nothing (rule 1), so a Domain "pricing service" that called the catalog directly would drag a
/// repository into the inner layer. The proration arithmetic itself is the ordinary plain multiply
/// <see cref="Application.UseCases.PurchaseAdministratorSlot.PurchaseAdministratorSlotHandler"/>'s own
/// remarks already use for a flat (non-banded) add-on - no
/// <see cref="Domain.SubscriptionTierBands.ComputeSeatPriceRub"/> here either, for the identical
/// reason.</para>
///
/// <para><b>Unlike the Administrator-slot purchase, there is no "old price" to subtract.</b> Buying a
/// second extra Administrator charges only the marginal difference over what was already being paid;
/// a channel add-on has no such history to net against - each <see cref="ChannelKind"/> is its own,
/// brand-new <see cref="BillingSubscription"/> option row (<see cref="IChannelAddOnPurchaseApplier"/>'s
/// own remarks), never a running count on an existing one, so the full currently-effective price is
/// prorated for the remainder of the base's period, exactly the shape a *first* Administrator-slot
/// purchase (nothing bought before) already takes.</para>
/// </summary>
public sealed class PurchaseChannelAddOnHandler(
    IBillingSubscriptionRepository subscriptions,
    IPermissionChecker permissions,
    IYooKassaPaymentsClient yooKassa,
    IPriceCatalogRepository prices,
    IChannelAddOnPurchaseApplier applier,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<PurchaseChannelAddOnResult>> HandleAsync(
        PurchaseChannelAddOn command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's billing.");
        }

        var baseSubscription = await subscriptions.GetByIdAsync(command.BaseSubscriptionId, command.SiteId, cancellationToken);
        if (baseSubscription is null)
        {
            return ConversationErrors.BillingSubscriptionNotFound(command.BaseSubscriptionId.Value);
        }

        // `26-278`'s own defensive check, matching this codebase's own "there is no such thing as a
        // validated-somewhere-else entity" posture (BillingSubscription.MarkSucceeded's own IsOption/
        // IsBase guard makes the identical distinction for the row itself): the endpoint's own route
        // names a *base* subscription id, and only a base subscription has a stored payment method and
        // a period this purchase can align a brand-new option's own period to - an option row buying a
        // second option is not a shape adr/0159 ever describes.
        if (baseSubscription.IsOption)
        {
            return ConversationErrors.BillingSubscriptionNotActive(
                $"Billing subscription {command.BaseSubscriptionId.Value} is itself an option subscription, not a "
                + "base subscription, and cannot sponsor a channel add-on purchase.");
        }

        // The identical "only a currently-healthy subscription may change what it is paying for" guard
        // PurchaseAdministratorSlotHandler's own remarks give for the analogous purchase.
        if (baseSubscription.Status != BillingSubscriptionStatus.Succeeded)
        {
            return ConversationErrors.BillingSubscriptionNotActive(
                $"Billing subscription {command.BaseSubscriptionId.Value} is {baseSubscription.Status}, not Succeeded, "
                + "and cannot purchase a channel add-on.");
        }

        if (baseSubscription.CurrentPeriodEnd is not { } periodEnd)
        {
            // Unreachable - a Succeeded row always has one (MarkSucceeded sets it unconditionally,
            // regardless of whether a payment method was ever saved), the identical guard
            // PurchaseAdministratorSlotHandler.HandleAsync's own remarks describe.
            throw new InvalidOperationException(
                $"Billing subscription {command.BaseSubscriptionId.Value} is Succeeded but has no period end.");
        }

        // `26-299`: reachable now that `savePaymentMethod` is the operator's own checkout-time choice -
        // see PurchaseAdministratorSlotHandler's own identical guard for the full reasoning.
        if (baseSubscription.PaymentMethodId is not { Length: > 0 } paymentMethodId)
        {
            return ConversationErrors.BillingNoStoredPaymentMethod(command.BaseSubscriptionId.Value);
        }

        var optionKey = ChannelEntitlementOptionKeys.For(command.ChannelKind);

        // The currently-effective price - what this purchase costs, read fresh, never cached, the
        // identical discipline every other real charge site in this codebase uses.
        var price = await prices.FindCurrentAsync(ChannelAddOnPricing.ChannelAddOnKey, cancellationToken);
        if (price is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(ChannelAddOnPricing.ChannelAddOnKey.Value);
        }

        var now = clock.UtcNow;
        var periodStart = periodEnd - BillingSubscription.PeriodLength;

        // No "old price" side to net against - see this handler's own remarks for why a channel add-on
        // is always a brand-new option row, never a running count on an existing one. `26-299`:
        // BillingProration.Prorate, not a hand-rolled remaining-days fraction - see that type's own
        // remarks for the floor rule this now applies.
        var proratedAmount = BillingProration.Prorate(price.AmountRub, now, periodStart, BillingSubscription.PeriodLength);

        var newOptionId = new BillingSubscriptionId(idGenerator.NewId(now));
        var idempotenceKey = idGenerator.NewId(now).ToString();
        var description = $"AGO Chat - {command.ChannelKind} channel add-on (prorated)";
        var chargeResult = await yooKassa.ChargeStoredPaymentMethodAsync(
            new ChargeStoredPaymentMethodRequest(proratedAmount, description, paymentMethodId, idempotenceKey), cancellationToken);

        // The identical exhaustive-switch shape ProcessSubscriptionRenewalHandler's own charge-result
        // handling already uses, rather than an `is not Success` negative check - this result type gains
        // a third case only over this handler's own dead body (it is a closed hierarchy, `IYooKassaPaymentsClient`'s
        // own remarks), so a future case is a compile-time-safe `default` throw here, never a silently
        // wrong cast.
        switch (chargeResult)
        {
            case ChargeStoredPaymentMethodResult.Success success:
                await applier.ApplyPurchaseAsync(
                    new ChannelAddOnPurchaseApplyRequest(
                        newOptionId, command.SiteId, command.ChannelKind, optionKey, success.PaymentId, paymentMethodId, periodEnd, now),
                    cancellationToken);
                return new PurchaseChannelAddOnResult(proratedAmount, command.ChannelKind, newOptionId);

            case ChargeStoredPaymentMethodResult.Refused refused:
                return ConversationErrors.BillingPaymentProviderRefused(refused.Reason);

            default:
                throw new InvalidOperationException($"Unhandled {nameof(ChargeStoredPaymentMethodResult)} case: {chargeResult.GetType().Name}.");
        }
    }
}
