using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.PreviewBillingPurchase;

/// <summary>
/// `26-299`: a plain read, never a charge - no <see cref="IYooKassaPaymentsClient"/> dependency at all,
/// unlike every purchase handler this mirrors. `CLAUDE.md` rule 8 does not forbid this reading
/// <see cref="IPriceCatalogRepository"/> the identical "read fresh, never cache" way a real purchase does;
/// it forbids caching what a write decision depends on, and this handler never writes anything - a second,
/// truly cached read here would only ever risk answering a number an actual purchase would not honour,
/// which is precisely the failure this handler exists to prevent (<c>PreviewBillingPurchase</c>'s own
/// remarks: "so 'preview' and 'charge' can never honestly disagree").
///
/// <para><b>The arithmetic below deliberately mirrors, rather than calls into,
/// <see cref="Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeatsHandler"/>/
/// <see cref="Application.UseCases.PurchaseAdministratorSlot.PurchaseAdministratorSlotHandler"/>/
/// <see cref="Application.UseCases.PurchaseChannelAddOn.PurchaseChannelAddOnHandler"/>'s own price-lookup
/// shape.</b> Extracting one shared calculator was considered and set aside for this item: three call
/// sites already read almost, but not quite, the same pair of prices (seats reads two keys and a banded
/// formula; Administrators and channels each read one flat key) and refactoring all three into one
/// abstraction is a real, separable improvement this item's own report flags rather than folds in here
/// under a migration-lane change already touching money-handling code in five places.</para>
/// </summary>
public sealed class PreviewBillingPurchaseHandler(
    IBillingSubscriptionRepository subscriptions, IPermissionChecker permissions, IPriceCatalogRepository prices, IClock clock)
{
    public async Task<Result<BillingPurchasePreviewResult>> HandleAsync(
        PreviewBillingPurchase query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's billing.");
        }

        var subscription = await subscriptions.GetByIdAsync(query.SubscriptionId, query.SiteId, cancellationToken);
        if (subscription is null)
        {
            return ConversationErrors.BillingSubscriptionNotFound(query.SubscriptionId.Value);
        }

        if (subscription.IsOption)
        {
            return ConversationErrors.BillingSubscriptionNotActive(
                $"Billing subscription {query.SubscriptionId.Value} is an option subscription, not the account's base "
                + "subscription - a purchase preview is always computed against the base.");
        }

        if (subscription.Status != BillingSubscriptionStatus.Succeeded)
        {
            return ConversationErrors.BillingSubscriptionNotActive(
                $"Billing subscription {query.SubscriptionId.Value} is {subscription.Status}, not Succeeded, and has nothing to preview a purchase against.");
        }

        if (subscription.CurrentPeriodEnd is not { } periodEnd)
        {
            // Unreachable - a Succeeded row always has one (MarkSucceeded sets it unconditionally).
            throw new InvalidOperationException($"Billing subscription {query.SubscriptionId.Value} is Succeeded but has no period end.");
        }

        var now = clock.UtcNow;
        var periodStart = periodEnd - BillingSubscription.PeriodLength;

        return query.Kind switch
        {
            BillingPurchaseKind.Seats => await PreviewSeatsAsync(subscription, query.RequestedSeats, now, periodStart, periodEnd, cancellationToken),
            BillingPurchaseKind.Administrators => await PreviewAdministratorsAsync(
                subscription, query.RequestedExtraAdministrators, now, periodStart, periodEnd, cancellationToken),
            BillingPurchaseKind.Channel => await PreviewChannelAsync(subscription, query.ChannelKind, now, periodStart, periodEnd, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Kind, $"Unhandled {nameof(BillingPurchaseKind)} case."),
        };
    }

    private async Task<Result<BillingPurchasePreviewResult>> PreviewSeatsAsync(
        BillingSubscription subscription, int? requestedSeats, DateTimeOffset now, DateTimeOffset periodStart, DateTimeOffset periodEnd,
        CancellationToken cancellationToken)
    {
        if (requestedSeats is not { } seats)
        {
            return ConversationErrors.BillingPreviewRequestInvalid("RequestedSeats is required to preview a Seats purchase.");
        }

        if (!SubscriptionTierBands.TryResolveTier(seats, out _))
        {
            return ConversationErrors.BillingInvalidSeatCount(
                $"{seats} seats is not a purchasable seat count - expected {SubscriptionTierBands.MinSeats}-{SubscriptionTierBands.MaxSeats}.");
        }

        if (seats <= subscription.RequestedSeats)
        {
            return ConversationErrors.BillingSeatCountUnchanged();
        }

        var oldBasePrice = await prices.FindVersionAsync(SubscriptionTierBands.BaseSeatPriceKey, subscription.BaseSeatPriceVersion, cancellationToken);
        var oldExtraPrice = await prices.FindVersionAsync(SubscriptionTierBands.ExtraSeatPriceKey, subscription.ExtraSeatPriceVersion, cancellationToken);
        if (oldBasePrice is null || oldExtraPrice is null)
        {
            // Unreachable in a correctly configured deployment - see ChangeSubscriptionSeatsHandler's
            // own identical guard for the full reasoning.
            throw new InvalidOperationException(
                $"Billing subscription {subscription.Id.Value}'s own stored price version "
                + $"({subscription.BaseSeatPriceVersion}/{subscription.ExtraSeatPriceVersion}) no longer exists - a published "
                + "price version must never be deleted.");
        }

        var newBasePrice = await prices.FindCurrentAsync(SubscriptionTierBands.BaseSeatPriceKey, cancellationToken);
        if (newBasePrice is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.BaseSeatPriceKey.Value);
        }

        var newExtraPrice = await prices.FindCurrentAsync(SubscriptionTierBands.ExtraSeatPriceKey, cancellationToken);
        if (newExtraPrice is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.ExtraSeatPriceKey.Value);
        }

        var oldPrice = SubscriptionTierBands.ComputeSeatPriceRub(subscription.RequestedSeats, oldBasePrice.AmountRub, oldExtraPrice.AmountRub);
        var newPrice = SubscriptionTierBands.ComputeSeatPriceRub(seats, newBasePrice.AmountRub, newExtraPrice.AmountRub);
        var delta = newPrice - oldPrice;
        var chargedNow = BillingProration.Prorate(delta, now, periodStart, BillingSubscription.PeriodLength);

        return new BillingPurchasePreviewResult(chargedNow, delta, periodEnd);
    }

    private async Task<Result<BillingPurchasePreviewResult>> PreviewAdministratorsAsync(
        BillingSubscription subscription, int? requestedExtraAdministrators, DateTimeOffset now, DateTimeOffset periodStart,
        DateTimeOffset periodEnd, CancellationToken cancellationToken)
    {
        if (requestedExtraAdministrators is not { } requested)
        {
            return ConversationErrors.BillingPreviewRequestInvalid(
                "RequestedExtraAdministrators is required to preview an Administrators purchase.");
        }

        if (requested <= subscription.ExtraAdministratorsPurchased)
        {
            return ConversationErrors.BillingAdministratorCountNotAnIncrease();
        }

        var oldPriceRub = 0m;
        if (subscription.ExtraAdministratorsPurchased > 0)
        {
            var oldPrice = await prices.FindVersionAsync(SubscriptionTierBands.AdminExtraPriceKey, subscription.AdminExtraPriceVersion, cancellationToken);
            if (oldPrice is null)
            {
                // Unreachable in a correctly configured deployment - see PurchaseAdministratorSlotHandler's
                // own identical guard for the full reasoning.
                throw new InvalidOperationException(
                    $"Billing subscription {subscription.Id.Value}'s own stored Administrator price version "
                    + $"({subscription.AdminExtraPriceVersion}) no longer exists - a published price version must never be deleted.");
            }

            oldPriceRub = oldPrice.AmountRub;
        }

        var newPrice = await prices.FindCurrentAsync(SubscriptionTierBands.AdminExtraPriceKey, cancellationToken);
        if (newPrice is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.AdminExtraPriceKey.Value);
        }

        var oldMonthlyCost = subscription.ExtraAdministratorsPurchased * oldPriceRub;
        var newMonthlyCost = requested * newPrice.AmountRub;
        var delta = newMonthlyCost - oldMonthlyCost;
        var chargedNow = BillingProration.Prorate(delta, now, periodStart, BillingSubscription.PeriodLength);

        return new BillingPurchasePreviewResult(chargedNow, delta, periodEnd);
    }

    private async Task<Result<BillingPurchasePreviewResult>> PreviewChannelAsync(
        BillingSubscription subscription, ChannelKind? channelKind, DateTimeOffset now, DateTimeOffset periodStart, DateTimeOffset periodEnd,
        CancellationToken cancellationToken)
    {
        if (channelKind is not { } kind)
        {
            return ConversationErrors.BillingPreviewRequestInvalid("ChannelKind is required to preview a Channel purchase.");
        }

        var optionKey = ChannelEntitlementOptionKeys.For(kind);
        var existingOptions = await subscriptions.ListOptionsForSiteAsync(subscription.SiteId, cancellationToken);
        var alreadyConnected = existingOptions.Any(o => o.Status == BillingSubscriptionStatus.Succeeded && o.OptionKey == optionKey);
        if (alreadyConnected)
        {
            return ConversationErrors.BillingChannelAlreadyConnected(kind.ToString());
        }

        var price = await prices.FindCurrentAsync(ChannelAddOnPricing.ChannelAddOnKey, cancellationToken);
        if (price is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(ChannelAddOnPricing.ChannelAddOnKey.Value);
        }

        var chargedNow = BillingProration.Prorate(price.AmountRub, now, periodStart, BillingSubscription.PeriodLength);

        return new BillingPurchasePreviewResult(chargedNow, price.AmountRub, periodEnd);
    }
}
