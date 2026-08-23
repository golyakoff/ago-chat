using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Insert-only, matching the port's own remarks - no caller updates a delivery in place yet
/// (`6-05`'s job, when it exists).</summary>
public sealed class WebhookDeliveryRepository(AgoChatDbContext db) : IWebhookDeliveryRepository
{
    public async Task SaveAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        if (db.Entry(delivery).State == EntityState.Detached)
        {
            db.WebhookDeliveries.Add(delivery);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
