using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`25-84`: <see cref="IDownloadOverageChargeRepository"/>'s one write - the identical
/// add-if-new/save shape <see cref="BillingSubscriptionRepository"/> uses, with no update branch at
/// all: this port only ever inserts (see that port's own remarks on why promoting a row belongs to the
/// appliers that own the surrounding transaction).</summary>
public sealed class DownloadOverageChargeRepository(AgoChatDbContext db) : IDownloadOverageChargeRepository
{
    public async Task SaveAsync(DownloadOverageCharge charge, CancellationToken cancellationToken)
    {
        db.DownloadOverageCharges.Add(charge);
        await db.SaveChangesAsync(cancellationToken);
    }
}
