using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-03`: the recurring-charge job's own commit step, called by
/// <c>Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewalHandler</c>
/// only after any outbound ЮKassa call the branch needed has already returned - the identical "charge
/// first, commit the verified outcome second, never the other way round" discipline `13-02`'s own
/// checkout path and webhook applier both already establish. Every method here reloads the row fresh
/// inside its own transaction rather than trusting whatever the handler's own earlier read saw - the
/// handler's read only ever decides <i>which</i> outbound call (if any) to make; the transaction is
/// what actually decides state, so it must not act on a snapshot that may be a Worker tick old.
/// </summary>
public interface ISubscriptionRenewalApplier
{
    /// <summary>The 7-day retry window closed with nothing recovered, or a cancelled subscription
    /// reached its own paid-through period end - <see cref="BillingSubscription.MarkLapsed"/> plus the
    /// same-transaction downgrade to `tier='free'`/`seat_limit=1` (<see cref="Site.ActivateSubscription"/>).
    /// No outbound call precedes this - `decisions/0006`'s "no charge attempt, successful or otherwise".</summary>
    Task ApplyLapseAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>A renewal or retry charge succeeded - <see cref="BillingSubscription.RecordRenewalSuccess"/>,
    /// and, only if that call actually applied a pending deferred downgrade, the matching same-transaction
    /// `Site.Tier`/`Site.SeatLimit` write. No new <c>payment_method_id</c> to record - a charge-on-file
    /// call reuses the one already stored and ЮKassa's own response carries no replacement, unlike
    /// `13-02`'s first-payment webhook.</summary>
    Task ApplyRenewalSuccessAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>A renewal or retry charge was refused - <see cref="BillingSubscription.RecordRenewalFailure"/>
    /// (first failure, from <c>Succeeded</c>) or <see cref="BillingSubscription.RecordRenewalRetryFailure"/>
    /// (a later retry, already <c>PastDue</c>), decided fresh from the row's own current status inside this
    /// transaction. `Site.Tier`/`Site.SeatLimit` are never touched on this path.</summary>
    Task ApplyRenewalFailureAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken);
}
