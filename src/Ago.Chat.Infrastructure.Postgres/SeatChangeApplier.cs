using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `13-03`: <see cref="ISeatChangeApplier"/>'s own implementation - the same "reload fresh inside this
/// transaction, mutate both aggregates, stage the outbox row, commit together" shape
/// <see cref="BillingWebhookApplier"/>/<see cref="SubscriptionRenewalApplier"/> already establish for
/// the analogous multi-aggregate writes, called only once <see cref="ChangeSubscriptionSeatsHandler"/>'s
/// own prorated charge has already succeeded.
/// </summary>
public sealed class SeatChangeApplier(AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator) : ISeatChangeApplier
{
    public async Task ApplyImmediateIncreaseAsync(SeatChangeApplyRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var subscription = await db.BillingSubscriptions.FirstOrDefaultAsync(s => s.Id == request.SubscriptionId, cancellationToken);
        if (subscription is null)
        {
            throw new InvalidOperationException(
                $"Billing subscription {request.SubscriptionId.Value} was not found while applying an immediate seat increase.");
        }

        subscription.ApplySeatIncreaseImmediately(request.NewSeatCount, request.NewTier);

        var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == request.SiteId, cancellationToken);
        if (site is null)
        {
            throw new InvalidOperationException(
                $"Site {request.SiteId.Value} was not found while applying an immediate seat increase - a foreign key should have prevented this.");
        }

        site.ActivateSubscription(request.NewTier, request.NewSeatCount, request.Now);
        var activated = site.DomainEvents.OfType<SiteSubscriptionActivated>().Single();
        outbox.Enqueue(SiteSubscriptionActivatedMapper.ToEnvelope(activated, idGenerator));
        site.ClearDomainEvents();

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
