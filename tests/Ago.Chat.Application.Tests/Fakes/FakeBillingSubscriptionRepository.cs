using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeBillingSubscriptionRepository : IBillingSubscriptionRepository
{
    public List<BillingSubscription> Saved { get; } = [];

    public Task SaveAsync(BillingSubscription subscription, CancellationToken cancellationToken)
    {
        Saved.Add(subscription);
        return Task.CompletedTask;
    }
}
