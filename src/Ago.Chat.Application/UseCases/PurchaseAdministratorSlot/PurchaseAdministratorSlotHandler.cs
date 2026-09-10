using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.PurchaseAdministratorSlot;

/// <summary>
/// `25-41`: `ChangeSubscriptionSeatsHandler.ApplyUpgradeAsync`'s own shape, restated for a flat
/// Administrator-slot purchase rather than a banded seat one - the same prorated, charge-then-apply
/// discipline `decisions/0006` established, computed with a plain multiply instead of
/// <see cref="SubscriptionTierBands.ComputeSeatPriceRub"/> (which does not apply here -
/// `ago-business` decision `0012` prices every extra Administrator identically, never by count).
///
/// <para><b>`25-43`: <c>oldPrice</c> is read from the subscription's own stored price version, never
/// from the catalog's currently-effective one</b> - the identical correctness reason
/// <see cref="Domain.BillingSubscription.AdminExtraPriceVersion"/> exists at all, restating
/// <see cref="ChangeSubscriptionSeats.ChangeSubscriptionSeatsHandler"/>'s own remarks for this handler:
/// recomputing "what the tenant is already paying" from today's currently-effective price would
/// silently reprice the period already paid for. Unlike seats, a subscription that has never bought an
/// extra Administrator has no stored version to look up at all (<see cref="Domain.BillingSubscription.AdminExtraPriceVersion"/>
/// starts at `0`, a sequence no version was ever minted with) - that case is not an error, it is
/// "nothing was being charged for this before", so the old-price lookup is skipped entirely rather than
/// resolved and found missing, and the old contribution is exactly zero.</para>
/// </summary>
public sealed class PurchaseAdministratorSlotHandler(
    IBillingSubscriptionRepository subscriptions,
    IPermissionChecker permissions,
    IYooKassaPaymentsClient yooKassa,
    IPriceCatalogRepository prices,
    IAdministratorSlotChangeApplier applier,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<PurchaseAdministratorSlotResult>> HandleAsync(
        PurchaseAdministratorSlot command, CancellationToken cancellationToken)
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

        // The identical "only a currently-healthy subscription may change what it is paying for" guard
        // ChangeSubscriptionSeatsHandler's own remarks give for the analogous seat change.
        if (subscription.Status != BillingSubscriptionStatus.Succeeded)
        {
            return ConversationErrors.BillingSubscriptionNotActive(
                $"Billing subscription {command.SubscriptionId.Value} is {subscription.Status}, not Succeeded, and cannot purchase an Administrator slot.");
        }

        if (command.RequestedExtraAdministrators <= subscription.ExtraAdministratorsPurchased)
        {
            return ConversationErrors.BillingAdministratorCountNotAnIncrease();
        }

        if (subscription.PaymentMethodId is not { Length: > 0 } paymentMethodId || subscription.CurrentPeriodEnd is not { } periodEnd)
        {
            // Unreachable - a Succeeded row always has both (MarkSucceeded sets them together), the
            // identical guard ChangeSubscriptionSeatsHandler.ApplyUpgradeAsync's own remarks describe.
            throw new InvalidOperationException(
                $"Billing subscription {command.SubscriptionId.Value} is Succeeded but has no payment method or period end.");
        }

        // `25-43`: the price this subscription was actually last charged for this key, or nothing at
        // all if it never bought one before - see this handler's own remarks for why a `0` stored
        // version is "nothing to look up", not an error.
        decimal oldPriceRub = 0m;
        if (subscription.ExtraAdministratorsPurchased > 0)
        {
            var oldPrice = await prices.FindVersionAsync(
                SubscriptionTierBands.AdminExtraPriceKey, subscription.AdminExtraPriceVersion, cancellationToken);
            if (oldPrice is null)
            {
                // Unreachable in a correctly configured deployment - a stored version number this
                // mechanism minted and never deletes (Domain.PublishedPriceVersion's own "insert-only,
                // never removed" remarks). Thrown, not translated, the identical posture
                // ChangeSubscriptionSeatsHandler's own equivalent guard uses.
                throw new InvalidOperationException(
                    $"Billing subscription {command.SubscriptionId.Value}'s own stored Administrator price version "
                    + $"({subscription.AdminExtraPriceVersion}) no longer exists - a published price version must never be deleted.");
            }

            oldPriceRub = oldPrice.AmountRub;
        }

        // The currently-effective price - what this purchase costs going forward, read fresh, never
        // cached, the identical discipline every other real charge site uses.
        var newPrice = await prices.FindCurrentAsync(SubscriptionTierBands.AdminExtraPriceKey, cancellationToken);
        if (newPrice is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.AdminExtraPriceKey.Value);
        }

        var now = clock.UtcNow;
        var periodLengthDays = (decimal)BillingSubscription.PeriodLength.TotalDays;
        var remainingDays = Math.Clamp((decimal)(periodEnd - now).TotalDays, 0m, periodLengthDays);

        // Flat, never banded - `SubscriptionTierBands.ComputeSeatPriceRub` does not apply here
        // (this handler's own remarks, and `25-41`'s own corrected Scope bullet).
        var oldMonthlyCost = subscription.ExtraAdministratorsPurchased * oldPriceRub;
        var newMonthlyCost = command.RequestedExtraAdministrators * newPrice.AmountRub;
        var proratedAmount = Math.Round(
            (newMonthlyCost - oldMonthlyCost) * remainingDays / periodLengthDays, 2, MidpointRounding.AwayFromZero);

        var idempotenceKey = idGenerator.NewId(now).ToString();
        var description = $"AGO Chat - {command.RequestedExtraAdministrators} extra Administrator(s) (prorated)";
        var chargeResult = await yooKassa.ChargeStoredPaymentMethodAsync(
            new ChargeStoredPaymentMethodRequest(proratedAmount, description, paymentMethodId, idempotenceKey), cancellationToken);

        if (chargeResult is ChargeStoredPaymentMethodResult.Refused refused)
        {
            return ConversationErrors.BillingPaymentProviderRefused(refused.Reason);
        }

        await applier.ApplyImmediateIncreaseAsync(
            new AdministratorSlotChangeApplyRequest(
                command.SubscriptionId, command.SiteId, command.RequestedExtraAdministrators, newPrice.Sequence, now),
            cancellationToken);

        return new PurchaseAdministratorSlotResult(proratedAmount, command.RequestedExtraAdministrators);
    }
}
