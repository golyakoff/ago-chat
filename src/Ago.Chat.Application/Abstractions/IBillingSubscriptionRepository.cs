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

    /// <summary>`13-03`: this item's own first read path - `CancelSubscriptionHandler`/the mid-cycle
    /// seat-change handler both need to load a specific row (by its own id, scoped to the site the
    /// caller already proved they administer) before mutating it.</summary>
    Task<BillingSubscription?> GetByIdAsync(BillingSubscriptionId id, SiteId siteId, CancellationToken cancellationToken);

    /// <summary>`13-03`: the system caller's own lookup - the recurring-charge job resolves a due row
    /// straight from <see cref="ListDueForRenewalAsync"/>'s own id, with no operator-proven site to
    /// scope against (there is no operator in that call chain at all).</summary>
    Task<BillingSubscription?> GetByIdAsync(BillingSubscriptionId id, CancellationToken cancellationToken);

    /// <summary>`13-04`: a real gap found while building the console billing screen, named here rather
    /// than worked around - nothing before this let a caller ask "what subscription, if any, is this
    /// site's own checkout/cancel/seat-change history currently sitting on", and the console cannot
    /// show a tier, a seat count, or a pending-vs-confirmed state without an answer. The most recently
    /// created row for the site, whatever its <see cref="BillingSubscriptionStatus"/> - not filtered to
    /// <c>Succeeded</c>/<c>PastDue</c> - because a caller returning from ЮKassa's hosted checkout needs
    /// to see its own just-created <c>Pending</c> row precisely to poll it honestly, the same "never the
    /// redirect alone" discipline this row's own webhook-applied transition already established. At
    /// most one row is ever genuinely live for ordinary use (a new checkout is only ever started when no
    /// paid subscription already governs the site), so "most recent" and "the one that matters" agree in
    /// every case this item's own Scope needs to handle; a caller several checkouts deep after repeated
    /// lapses still gets the newest attempt, which is the one whose outcome is still undetermined.
    /// <see langword="null"/> for a site that has never started a checkout at all (still on the free
    /// tier by construction, `13-01`'s own default).</summary>
    Task<BillingSubscription?> GetLatestForSiteAsync(SiteId siteId, CancellationToken cancellationToken);

    /// <summary>`13-03`: the recurring-charge job's own candidate list - every row a `Succeeded`
    /// renewal or a `PastDue` retry is owed right now (<see cref="BillingSubscription.IsDueForRenewal"/>/
    /// <see cref="BillingSubscription.IsRetryDue"/>'s own predicates, expressed as one `WHERE` clause so
    /// the job never loads a row it has nothing to do with). Bounded, the same reason every other sweep
    /// job's own candidate query is (`AutoCloseInactiveConversationsQuery`'s own remarks) - an unbounded
    /// scan is one bad month away from being the incident.</summary>
    Task<IReadOnlyList<BillingSubscriptionId>> ListDueForRenewalAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>`13-03`: persists a single-aggregate mutation (<c>RequestCancellation</c>,
    /// <c>ScheduleSeatDecrease</c>) on a row already loaded through <see cref="GetByIdAsync"/> in this
    /// same request - the identical "always already tracked, `SaveChangesAsync` alone is enough"
    /// contract <c>IOperatorRepository.SaveAsync</c>'s own remarks describe. Never used for a write that
    /// also touches `Site` - that is a different, wider transaction (`ISeatChangeApplier`/
    /// `ISubscriptionRenewalApplier`), the same "its own port because it writes across more than one
    /// aggregate" split this codebase draws everywhere else.</summary>
    Task UpdateAsync(BillingSubscription subscription, CancellationToken cancellationToken);
}
