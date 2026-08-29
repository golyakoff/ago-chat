using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `13-02`: the one database transaction this item's backlog describes, in full - verify happens
/// earlier (the endpoint, before this class is ever called); everything from "check the idempotency
/// ledger" onward happens here, inside one <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/>.
/// The multi-aggregate write shape (`BillingWebhookEvent` + `BillingSubscription` + `Site`, committed
/// together or not at all) is the same "its own port because it writes across more than one aggregate"
/// reasoning <c>SiteRegistrationRepository</c>/<c>OperatorInviteRedemptionRepository</c> already
/// establish - unlike either of those, this one also has an outbox row to stage.
///
/// <para><b>Why the domain-event -&gt; outbox mapping is called from here, in Infrastructure, rather
/// than an Application handler - the one deliberate exception to this codebase's usual placement.</b>
/// Every other mapper call site (`UpdateWidgetConfigHandler`, etc.) lives in Application, because the
/// write there is a single-aggregate save with no explicit transaction to coordinate - `IOutboxWriter`'s
/// own contract is "stage on whichever `DbContext` is already tracking the caller's own change," and
/// the caller in every one of those cases is the Application handler itself, one `SaveChangesAsync`
/// away from committing. Here, the caller that actually owns the transaction is this class: splitting
/// the mapping call out to `ProcessYooKassaWebhookHandler` would force either a second, later
/// `SaveChangesAsync` outside this transaction (breaking the one-transaction guarantee this item's
/// backlog explicitly requires) or leaking the raw `IDbContextTransaction` back across the Application
/// boundary (a far more serious layering violation than a Mapping-class reference). This is legal under
/// the dependency rule as stated - Infrastructure may depend on Application, only the reverse is
/// forbidden (`LayeringTests.Application_DoesNotDependOnInfrastructureOrAnyHost`) - and is the same
/// "whichever caller owns the transaction is the one that stages the outbox row" principle
/// `IOutboxWriter`'s own remarks state, simply resolved in favour of Infrastructure this one time
/// because that is where the transaction genuinely lives.</para>
/// </summary>
public sealed class BillingWebhookApplier(AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator) : IBillingWebhookApplier
{
    private const string PaymentSucceededEvent = "payment.succeeded";

    private const string PaymentCanceledEvent = "payment.canceled";

    public async Task<BillingWebhookApplyResult> ApplyAsync(BillingWebhookApplyRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ledgerId = new BillingWebhookEventId(idGenerator.NewId(request.Now));
        db.BillingWebhookEvents.Add(
            BillingWebhookEvent.Record(ledgerId, request.YooKassaPaymentId, request.EventType, request.Now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The ledger row never landed - a redelivery of an event this exact (payment_id, event_type)
            // pair already recorded. Detach so a caller reusing this DbContext does not keep tracking a
            // phantom insert, the same shape WebhookDeliveryRepository.SaveAsync's own remarks describe.
            db.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return new BillingWebhookApplyResult.Duplicate();
        }

        var subscription = await db.BillingSubscriptions.FirstOrDefaultAsync(
            s => s.YooKassaPaymentId == request.YooKassaPaymentId, cancellationToken);
        if (subscription is null)
        {
            // The ledger row above still commits - a real redelivery of this exact event is still
            // caught as Duplicate next time, even though there is no subscription to act on now.
            await transaction.CommitAsync(cancellationToken);
            return new BillingWebhookApplyResult.SubscriptionNotFound();
        }

        switch (request.EventType)
        {
            case PaymentSucceededEvent:
                subscription.MarkSucceeded(request.PaymentMethodId, request.Now);

                var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == subscription.SiteId, cancellationToken);
                if (site is null)
                {
                    // A foreign key (BillingSubscriptionConfiguration.HasOne<Site>) should make this
                    // unreachable - a subscription cannot exist for a site row that has been deleted out
                    // from under it, and this codebase has no site-deletion path at all, the same
                    // "unreachable, thrown rather than translated" shape
                    // OperatorInviteRedemptionRepository.LockSiteAndReadSeatLimitAsync's own remarks
                    // describe for the identical situation.
                    throw new InvalidOperationException(
                        $"Site {subscription.SiteId.Value} was not found while applying a ЮKassa webhook - "
                        + "a foreign key should have prevented this.");
                }

                site.ActivateSubscription(subscription.Tier, subscription.RequestedSeats, request.Now);
                var activated = site.DomainEvents.OfType<SiteSubscriptionActivated>().Single();
                outbox.Enqueue(SiteSubscriptionActivatedMapper.ToEnvelope(activated, idGenerator));
                site.ClearDomainEvents();

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new BillingWebhookApplyResult.Applied(subscription.SiteId, subscription.Tier, subscription.RequestedSeats);

            case PaymentCanceledEvent:
                // Site.Tier/Site.SeatLimit are never touched here - this item's own Scope: "they were
                // never changed from free in the first place, since the pending row - not the site -
                // held the in-flight state."
                subscription.MarkFailed();
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new BillingWebhookApplyResult.Canceled();

            default:
                // A new, first-seen event of a type this item has no handling for - the ledger row
                // above already committed as this method's own record of having seen it.
                await transaction.CommitAsync(cancellationToken);
                return new BillingWebhookApplyResult.Ignored();
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
