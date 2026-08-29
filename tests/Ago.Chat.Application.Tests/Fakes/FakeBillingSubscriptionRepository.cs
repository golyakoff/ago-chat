using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeBillingSubscriptionRepository : IBillingSubscriptionRepository
{
    private readonly List<BillingSubscription> _all = [];

    public List<BillingSubscription> Saved { get; } = [];

    public List<BillingSubscription> Updated { get; } = [];

    public Task SaveAsync(BillingSubscription subscription, CancellationToken cancellationToken)
    {
        Saved.Add(subscription);
        _all.Add(subscription);
        return Task.CompletedTask;
    }

    public Task<BillingSubscription?> GetByIdAsync(BillingSubscriptionId id, SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult(_all.Find(s => s.Id == id && s.SiteId == siteId));

    public Task<BillingSubscription?> GetByIdAsync(BillingSubscriptionId id, CancellationToken cancellationToken) =>
        Task.FromResult(_all.Find(s => s.Id == id));

    /// <summary>Mirrors <c>BillingSubscriptionRepository.GetLatestForSiteAsync</c>'s own ordering
    /// exactly - most recently created row for the site, or <see langword="null"/>.</summary>
    public Task<BillingSubscription?> GetLatestForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult(_all.Where(s => s.SiteId == siteId).OrderByDescending(s => s.CreatedAt).FirstOrDefault());

    /// <summary>Mirrors <c>BillingSubscriptionRepository.ListDueForRenewalAsync</c>'s own predicate
    /// exactly - a fake that quietly used a different rule than the adapter would let a test pass
    /// against a condition production does not implement.</summary>
    public Task<IReadOnlyList<BillingSubscriptionId>> ListDueForRenewalAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var retryThreshold = now - BillingSubscription.RetryInterval;
        var due = _all
            .Where(s =>
                (s.Status == BillingSubscriptionStatus.Succeeded && s.CurrentPeriodEnd is { } periodEnd && periodEnd <= now)
                || (s.Status == BillingSubscriptionStatus.PastDue
                    && (s.LastRenewalAttemptAt is null || s.LastRenewalAttemptAt <= retryThreshold)))
            .OrderBy(s => s.CreatedAt)
            .Take(batchSize)
            .Select(s => s.Id)
            .ToList();

        return Task.FromResult<IReadOnlyList<BillingSubscriptionId>>(due);
    }

    public Task UpdateAsync(BillingSubscription subscription, CancellationToken cancellationToken)
    {
        Updated.Add(subscription);
        return Task.CompletedTask;
    }

    public void Seed(BillingSubscription subscription) => _all.Add(subscription);
}
