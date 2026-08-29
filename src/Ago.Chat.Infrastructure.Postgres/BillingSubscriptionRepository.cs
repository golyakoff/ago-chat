using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`13-02`'s own insert-only `SaveAsync` (`CreateCheckoutSessionHandler`'s only caller),
/// extended by `13-03` with the read/update paths its own new use cases need - see each member's own
/// remarks on the port for why they are shaped the way they are.</summary>
public sealed class BillingSubscriptionRepository(AgoChatDbContext db) : IBillingSubscriptionRepository
{
    public async Task SaveAsync(BillingSubscription subscription, CancellationToken cancellationToken)
    {
        db.BillingSubscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<BillingSubscription?> GetByIdAsync(BillingSubscriptionId id, SiteId siteId, CancellationToken cancellationToken) =>
        db.BillingSubscriptions.FirstOrDefaultAsync(s => s.Id == id && s.SiteId == siteId, cancellationToken);

    public Task<BillingSubscription?> GetByIdAsync(BillingSubscriptionId id, CancellationToken cancellationToken) =>
        db.BillingSubscriptions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<BillingSubscriptionId>> ListDueForRenewalAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var retryThreshold = now - BillingSubscription.RetryInterval;

        return await db.BillingSubscriptions
            .Where(s =>
                (s.Status == BillingSubscriptionStatus.Succeeded && s.CurrentPeriodEnd != null && s.CurrentPeriodEnd <= now)
                || (s.Status == BillingSubscriptionStatus.PastDue
                    && (s.LastRenewalAttemptAt == null || s.LastRenewalAttemptAt <= retryThreshold)))
            .OrderBy(s => s.CreatedAt)
            .Take(batchSize)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The entity must already be tracked (loaded through one of the <c>GetByIdAsync</c>
    /// overloads above, in this same request) - the identical "always already tracked" contract
    /// <c>OperatorRepository.SaveAsync</c>'s own remarks describe.</summary>
    public async Task UpdateAsync(BillingSubscription subscription, CancellationToken cancellationToken) =>
        await db.SaveChangesAsync(cancellationToken);
}
