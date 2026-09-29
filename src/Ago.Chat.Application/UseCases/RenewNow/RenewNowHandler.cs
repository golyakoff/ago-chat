using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RenewNow;

/// <summary>
/// `26-296`: pay-early, reusing every piece of the automatic renewal machinery rather than opening a
/// second path beside it - the same recurring-amount arithmetic <c>ProcessSubscriptionRenewalHandler</c>
/// uses (seats plus purchased extra Administrators, overage excluded), the same
/// <see cref="ISubscriptionRenewalApplier.ApplyRenewalSuccessAsync"/> commit (so `Site.Tier`/
/// `Site.SeatLimit` stay correct if a deferred downgrade happens to land on this same call), and the
/// same deterministic <c>renewal:{id}:{date}</c> idempotence key the automatic sweep already uses.
///
/// <para><b>Why the same idempotence key, precisely.</b> Sharing it is what makes a pay-early click that
/// races the automatic sweep for the same due row collapse into one real ЮKassa charge rather than two -
/// the identical "two-replica race is safe" guarantee `ChargeStoredPaymentMethodRequest`'s own remarks
/// already describe, now extended to a person racing the Worker instead of two Worker replicas racing
/// each other.</para>
///
/// <para><b>ЮKassa's own idempotence key is not enough on its own, so this handler adds a same-day
/// guard before ever calling it.</b> A same-day double-fire (a double-click, or a genuine race with the
/// sweep) would make ЮKassa return the identical cached `Success` twice, and a naive handler would then
/// call <see cref="ISubscriptionRenewalApplier.ApplyRenewalSuccessAsync"/> twice - extending
/// <see cref="BillingSubscription.CurrentPeriodEnd"/> by two periods for one real charge. Refusing with
/// <see cref="ConversationErrors.BillingSubscriptionAlreadyRenewedToday"/> the moment
/// <see cref="BillingSubscription.LastRenewalAttemptAt"/> already carries today's date - checked before
/// any outbound call - is what keeps the domain-side effect exactly as idempotent as the money
/// itself.</para>
///
/// <para><b>A refused charge deliberately does NOT call <see cref="ISubscriptionRenewalApplier.ApplyRenewalFailureAsync"/>-
/// the one place this handler diverges from the automatic sweep, and worth stating plainly.</b> That
/// method transitions a <c>Succeeded</c> row to <c>PastDue</c>, which is the correct reaction to a
/// charge that was actually <i>owed</i>. Pay-early is optional: the subscription is still paid through
/// its own <see cref="BillingSubscription.CurrentPeriodEnd"/> regardless of whether this voluntary early
/// charge succeeds, so failing it must never start the 7-day <see cref="BillingSubscription.PastDueRetryWindow"/>
/// countdown over money nobody was yet owed. A refusal here leaves the row exactly as it was; the
/// operator may try again, later, or simply let the ordinary renewal run at the real period end.</para>
///
/// <para><b>Base subscriptions only.</b> An option row's own <see cref="BillingSubscription.OptionKey"/>
/// is checked and refused before any price read or charge, matching
/// `docs/backlog/26-290-console-billing-redesign.md`'s own "simplest first cut is base-only" - the
/// identical defensive shape <c>PurchaseChannelAddOnHandler</c>'s own base-subscription guard already
/// uses for the analogous distinction.</para>
/// </summary>
public sealed class RenewNowHandler(
    IBillingSubscriptionRepository subscriptions,
    IPermissionChecker permissions,
    IYooKassaPaymentsClient yooKassa,
    IPriceCatalogRepository prices,
    ISubscriptionRenewalApplier applier,
    IClock clock)
{
    public async Task<Result<RenewNowResult>> HandleAsync(RenewNow command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's billing.");
        }

        var subscription = await subscriptions.GetByIdAsync(command.SubscriptionId, command.SiteId, cancellationToken);
        if (subscription is null)
        {
            return ConversationErrors.BillingSubscriptionNotFound(command.SubscriptionId.Value);
        }

        if (subscription.IsOption)
        {
            return ConversationErrors.BillingSubscriptionNotActive(
                $"Billing subscription {command.SubscriptionId.Value} is an option subscription, not the account's base "
                + "subscription - only the base subscription may be paid early; an option renews on its own schedule.");
        }

        if (subscription.Status != BillingSubscriptionStatus.Succeeded)
        {
            return ConversationErrors.BillingSubscriptionNotActive(
                $"Billing subscription {command.SubscriptionId.Value} is {subscription.Status}, not Succeeded, and cannot be paid early.");
        }

        if (subscription.CurrentPeriodEnd is not { } currentPeriodEnd)
        {
            // Unreachable - MarkSucceeded always sets one (regardless of whether a payment method was
            // ever saved), the identical guard ProcessSubscriptionRenewalHandler's/
            // PurchaseChannelAddOnHandler's own remarks describe.
            throw new InvalidOperationException(
                $"Billing subscription {command.SubscriptionId.Value} is Succeeded but has no period end.");
        }

        // `26-299`: reachable now that `savePaymentMethod` is the operator's own checkout-time choice -
        // pay-early against nothing on file cannot possibly work, so this is an honest, mappable failure,
        // never the "unreachable, thrown" case this guard used to be.
        if (subscription.PaymentMethodId is not { Length: > 0 } paymentMethodId)
        {
            return ConversationErrors.BillingNoStoredPaymentMethod(command.SubscriptionId.Value);
        }

        var now = clock.UtcNow;

        // See this handler's own remarks for why a same-day check must run before any outbound call,
        // not merely rely on ЮKassa's own idempotence key. Compared as the identical "yyyy-MM-dd" string
        // the idempotence key itself is built from just below, not DateTimeOffset.Date - that property
        // returns a System.DateTime, and `Ago.Chat.Application` may never reference that type at all
        // (`TimeAndIdentityTests.DateTimeType_NeverAppearsOutsideInfrastructure`, `adr/0011`).
        if (subscription.LastRenewalAttemptAt is { } lastAttempt
            && lastAttempt.ToString("yyyy-MM-dd") == now.ToString("yyyy-MM-dd"))
        {
            return ConversationErrors.BillingSubscriptionAlreadyRenewedToday(command.SubscriptionId.Value);
        }

        var basePrice = await prices.FindCurrentAsync(SubscriptionTierBands.BaseSeatPriceKey, cancellationToken);
        if (basePrice is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.BaseSeatPriceKey.Value);
        }

        var extraPrice = await prices.FindCurrentAsync(SubscriptionTierBands.ExtraSeatPriceKey, cancellationToken);
        if (extraPrice is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.ExtraSeatPriceKey.Value);
        }

        var seatAmount = SubscriptionTierBands.ComputeSeatPriceRub(subscription.RequestedSeats, basePrice.AmountRub, extraPrice.AmountRub);

        var adminAmount = 0m;
        if (subscription.ExtraAdministratorsPurchased > 0)
        {
            var adminPrice = await prices.FindCurrentAsync(SubscriptionTierBands.AdminExtraPriceKey, cancellationToken);
            if (adminPrice is null)
            {
                return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.AdminExtraPriceKey.Value);
            }

            adminAmount = subscription.ExtraAdministratorsPurchased * adminPrice.AmountRub;
        }

        // `26-295`/`26-296`: the recurring total only - download overage is deliberately excluded, the
        // identical "variable, only known at an ordinary renewal's own moment, never invented ahead of
        // it" reasoning `GetBillingStatusHandler`'s own `nextChargeRub` remarks give for the identical
        // exclusion.
        var amount = seatAmount + adminAmount;
        var description = $"AGO Chat - {subscription.Tier} tier renewal (paid early), {subscription.RequestedSeats} seats";
        var idempotenceKey = $"renewal:{command.SubscriptionId.Value}:{now:yyyy-MM-dd}";

        var chargeResult = await yooKassa.ChargeStoredPaymentMethodAsync(
            new ChargeStoredPaymentMethodRequest(amount, description, paymentMethodId, idempotenceKey), cancellationToken);

        switch (chargeResult)
        {
            case ChargeStoredPaymentMethodResult.Success:
                await applier.ApplyRenewalSuccessAsync(
                    command.SubscriptionId, now, basePrice.Sequence, extraPrice.Sequence, [], cancellationToken);
                return new RenewNowResult(amount, currentPeriodEnd + BillingSubscription.PeriodLength);

            case ChargeStoredPaymentMethodResult.Refused refused:
                // Deliberately NOT ApplyRenewalFailureAsync - see this handler's own remarks in full.
                return ConversationErrors.BillingPaymentProviderRefused(refused.Reason);

            default:
                throw new InvalidOperationException($"Unhandled {nameof(ChargeStoredPaymentMethodResult)} case: {chargeResult.GetType().Name}.");
        }
    }
}
