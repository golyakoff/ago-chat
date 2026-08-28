using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-02`: the write side of checkout-session creation - a plain, insert-only single-aggregate port,
/// the same shape <c>IWebhookDeliveryRepository.SaveAsync</c> already establishes for its own
/// insert-only row. <see cref="BillingWebhookApplier"/> (a different, wider-transaction port) is what
/// later mutates the row this creates - deliberately not exposed here, the same "its own port because
/// it writes across more than one aggregate" split <c>ISiteRegistrationRepository</c>'s own remarks
/// describe for the analogous case.
/// </summary>
public interface IBillingSubscriptionRepository
{
    Task SaveAsync(BillingSubscription subscription, CancellationToken cancellationToken);
}
