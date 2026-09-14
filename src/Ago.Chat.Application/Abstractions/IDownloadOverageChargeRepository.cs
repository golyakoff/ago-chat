using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-84`: the write half of <c>download_overage_charges</c> - one aggregate, saved. Declared here and
/// implemented in `Ago.Chat.Infrastructure.Postgres` because the dependency rule forbids Application
/// knowing about `AgoChatDbContext`; the alternative - letting
/// <c>PurchaseDownloadOverageHandler</c> hold a `DbContext` - would make that handler untestable
/// without a database, which is exactly what `Ago.Chat.Application.Tests` relies on not being true.
///
/// <para><b>No "settle this month" method.</b> Promoting a pending checkout row and writing an invoice
/// settlement row both happen inside a transaction that already spans other aggregates -
/// <c>BillingWebhookApplier</c>'s own ledger-plus-subscription transaction and
/// <c>SubscriptionRenewalApplier</c>'s own renewal transaction respectively - so both belong to those
/// multi-aggregate appliers, the same "its own port because it writes across more than one aggregate"
/// reasoning <see cref="IBillingWebhookApplier"/>'s own remarks already give. This port exists for the
/// one write that genuinely stands alone: creating the pending row before the tenant is sent to the
/// hosted checkout page.</para>
/// </summary>
public interface IDownloadOverageChargeRepository
{
    Task SaveAsync(DownloadOverageCharge charge, CancellationToken cancellationToken);
}
