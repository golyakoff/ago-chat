using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Insert-only, matching the port's own remarks - `CreateCheckoutSessionHandler` is this
/// class's only caller.</summary>
public sealed class BillingSubscriptionRepository(AgoChatDbContext db) : IBillingSubscriptionRepository
{
    public async Task SaveAsync(BillingSubscription subscription, CancellationToken cancellationToken)
    {
        db.BillingSubscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);
    }
}
