using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.CancelSubscription;

/// <summary>
/// `13-03`/`decisions/0006`: a single-aggregate write - only <see cref="BillingSubscription.CancelRequested"/>
/// moves, nothing on <see cref="Site"/> changes here (the recurring-charge job is what acts on the flag,
/// at `current_period_end`), so this goes through the plain
/// <see cref="IBillingSubscriptionRepository.UpdateAsync"/> path rather than one of the multi-aggregate
/// appliers - the same "ordinary single-aggregate write, no shared transaction to coordinate" shape
/// `UpdateWidgetConfigHandler`'s own remarks describe for the analogous case.
/// </summary>
public sealed class CancelSubscriptionHandler(
    IBillingSubscriptionRepository subscriptions, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result<CancelSubscriptionResult>> HandleAsync(CancelSubscription command, CancellationToken cancellationToken)
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

        if (subscription.Status is not (BillingSubscriptionStatus.Succeeded or BillingSubscriptionStatus.PastDue))
        {
            return ConversationErrors.BillingSubscriptionNotActive(
                $"Billing subscription {command.SubscriptionId.Value} is {subscription.Status} and cannot be cancelled.");
        }

        subscription.RequestCancellation(clock.UtcNow);
        await subscriptions.UpdateAsync(subscription, cancellationToken);

        return new CancelSubscriptionResult(subscription.CurrentPeriodEnd);
    }
}

/// <summary><see cref="PaidThroughUntil"/> is what the operator's own console shows - "your paid tier
/// runs until this date, then downgrades" (`decisions/0006`'s own wording).</summary>
public sealed record CancelSubscriptionResult(DateTimeOffset? PaidThroughUntil);
