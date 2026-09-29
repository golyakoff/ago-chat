using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SetNextPeriodComposition;

/// <summary>
/// `26-299`: a single-aggregate write - only <see cref="BillingSubscription.PendingSeatCount"/>/
/// <see cref="BillingSubscription.PendingTier"/>/<see cref="BillingSubscription.PendingAdminCount"/> move
/// here, nothing on <see cref="Site"/> changes until a real renewal applies them
/// (<c>SubscriptionRenewalApplier</c>'s own remarks) - the identical "ordinary single-aggregate write, no
/// shared transaction to coordinate" shape <c>CancelSubscriptionHandler</c>'s own remarks describe for the
/// analogous write. No <c>IClock</c> dependency - unlike <c>RequestCancellation</c>,
/// <see cref="BillingSubscription.ScheduleNextPeriodComposition"/> records no timestamp of its own.
/// </summary>
public sealed class SetNextPeriodCompositionHandler(IBillingSubscriptionRepository subscriptions, IPermissionChecker permissions)
{
    public async Task<Result<SetNextPeriodCompositionResult>> HandleAsync(
        SetNextPeriodComposition command, CancellationToken cancellationToken)
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
                + "subscription - only the base subscription has a next-period seat/Administrator composition to plan.");
        }

        if (subscription.Status is not (BillingSubscriptionStatus.Succeeded or BillingSubscriptionStatus.PastDue))
        {
            return ConversationErrors.BillingSubscriptionNotActive(
                $"Billing subscription {command.SubscriptionId.Value} is {subscription.Status} and cannot schedule a next-period composition.");
        }

        if (!SubscriptionTierBands.TryResolveTier(command.RequestedSeats, out var tier))
        {
            return ConversationErrors.BillingInvalidSeatCount(
                $"{command.RequestedSeats} seats is not a purchasable seat count - expected "
                + $"{SubscriptionTierBands.MinSeats}-{SubscriptionTierBands.MaxSeats}.");
        }

        if (command.RequestedExtraAdministrators < 0)
        {
            return ConversationErrors.BillingInvalidAdministratorCount(
                "The requested extra-Administrator count cannot be negative.");
        }

        subscription.ScheduleNextPeriodComposition(command.RequestedSeats, tier, command.RequestedExtraAdministrators);
        await subscriptions.UpdateAsync(subscription, cancellationToken);

        return new SetNextPeriodCompositionResult(tier, command.RequestedSeats, command.RequestedExtraAdministrators);
    }
}
