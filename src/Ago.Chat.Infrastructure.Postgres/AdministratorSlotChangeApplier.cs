using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `25-41`: <see cref="IAdministratorSlotChangeApplier"/>'s own implementation - the identical "reload
/// fresh inside this transaction, mutate both aggregates, stage the outbox row, commit together" shape
/// <see cref="SeatChangeApplier"/> already establishes for the analogous seat purchase, called only
/// once <see cref="Application.UseCases.PurchaseAdministratorSlot.PurchaseAdministratorSlotHandler"/>'s
/// own prorated charge has already succeeded.
///
/// <para><b>No <see cref="IAdministratorLimitEnforcer"/> call here, unlike every other caller of
/// <see cref="Site.ActivateSubscription"/>.</b> This applier only ever increases
/// <see cref="BillingSubscription.ExtraAdministratorsPurchased"/> (<see cref="BillingSubscription.ApplyAdministratorPurchase"/>'s
/// own guard throws otherwise), so <see cref="Site.AdminLimit"/> can only go up here, never down - the
/// one precondition <see cref="IAdministratorLimitEnforcer.DemoteExcessAdministratorsAsync"/> exists to
/// react to can provably never hold at this call site, so it is not called, rather than called and
/// trusted to no-op - a reader should not have to check the enforcer's own guard to know this path
/// never demotes anybody.</para>
/// </summary>
public sealed class AdministratorSlotChangeApplier(AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator)
    : IAdministratorSlotChangeApplier
{
    public async Task ApplyImmediateIncreaseAsync(AdministratorSlotChangeApplyRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var subscription = await db.BillingSubscriptions.FirstOrDefaultAsync(s => s.Id == request.SubscriptionId, cancellationToken);
        if (subscription is null)
        {
            throw new InvalidOperationException(
                $"Billing subscription {request.SubscriptionId.Value} was not found while applying an Administrator slot purchase.");
        }

        subscription.ApplyAdministratorPurchase(request.NewExtraAdministratorCount, request.AdminExtraPriceVersion);

        var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == request.SiteId, cancellationToken);
        if (site is null)
        {
            throw new InvalidOperationException(
                $"Site {request.SiteId.Value} was not found while applying an Administrator slot purchase - a foreign key should have prevented this.");
        }

        site.ActivateSubscription(site.Tier, site.SeatLimit, request.NewExtraAdministratorCount, request.Now);
        var activated = site.DomainEvents.OfType<SiteSubscriptionActivated>().Single();
        outbox.Enqueue(SiteSubscriptionActivatedMapper.ToEnvelope(activated, idGenerator));
        site.ClearDomainEvents();

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
