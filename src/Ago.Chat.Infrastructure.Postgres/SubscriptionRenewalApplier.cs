using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `13-03`: <see cref="ISubscriptionRenewalApplier"/>'s own implementation - each method opens its own
/// transaction, reloads the row fresh inside it (the port's own remarks on why), mutates, and - only
/// for a branch that changes what the site is entitled to - stages the same `SiteSettingsChanged`
/// outbox row `BillingWebhookApplier` already produces for the analogous first-payment write, reusing
/// <see cref="SiteSubscriptionActivatedMapper"/> rather than inventing a second cache-invalidation
/// shape for what is, from a cache's own point of view, the identical fact ("this site's tier/seat
/// limit changed").
/// </summary>
public sealed class SubscriptionRenewalApplier(AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator) : ISubscriptionRenewalApplier
{
    public async Task ApplyLapseAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var subscription = await LoadOrThrowAsync(id, cancellationToken);
        subscription.MarkLapsed();

        var site = await LoadSiteOrThrowAsync(subscription.SiteId, cancellationToken);
        site.ActivateSubscription("free", 1, now);
        StageSiteSettingsChanged(site);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ApplyRenewalSuccessAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var subscription = await LoadOrThrowAsync(id, cancellationToken);
        var seatsBefore = subscription.RequestedSeats;
        var tierBefore = subscription.Tier;

        subscription.RecordRenewalSuccess(now, paymentMethodId: null);

        // A pending deferred downgrade only ever changes RequestedSeats/Tier inside RecordRenewalSuccess
        // itself - comparing before/after is this applier's own way of learning "did that happen" without
        // RecordRenewalSuccess needing to hand back a second return value nothing else would use.
        if (subscription.RequestedSeats != seatsBefore || subscription.Tier != tierBefore)
        {
            var site = await LoadSiteOrThrowAsync(subscription.SiteId, cancellationToken);
            site.ActivateSubscription(subscription.Tier, subscription.RequestedSeats, now);
            StageSiteSettingsChanged(site);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ApplyRenewalFailureAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var subscription = await LoadOrThrowAsync(id, cancellationToken);
        if (subscription.Status == BillingSubscriptionStatus.PastDue)
        {
            subscription.RecordRenewalRetryFailure(now);
        }
        else
        {
            subscription.RecordRenewalFailure(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<BillingSubscription> LoadOrThrowAsync(BillingSubscriptionId id, CancellationToken cancellationToken)
    {
        var subscription = await db.BillingSubscriptions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is null)
        {
            // The candidate list this applier's own caller (ProcessSubscriptionRenewalHandler) reads
            // from is a moment-old snapshot of the same table - a row it names must still exist by the
            // time this call runs, since nothing in this codebase ever deletes a billing_subscriptions
            // row. Thrown, not translated into a result case, the same "unreachable, thrown rather than
            // translated" shape BillingWebhookApplier's own missing-site guard describes.
            throw new InvalidOperationException($"Billing subscription {id.Value} was not found while applying a renewal outcome.");
        }

        return subscription;
    }

    private async Task<Site> LoadSiteOrThrowAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == siteId, cancellationToken);
        if (site is null)
        {
            throw new InvalidOperationException(
                $"Site {siteId.Value} was not found while applying a subscription renewal outcome - a foreign key should have prevented this.");
        }

        return site;
    }

    private void StageSiteSettingsChanged(Site site)
    {
        var activated = site.DomainEvents.OfType<SiteSubscriptionActivated>().Single();
        outbox.Enqueue(SiteSubscriptionActivatedMapper.ToEnvelope(activated, idGenerator));
        site.ClearDomainEvents();
    }
}
