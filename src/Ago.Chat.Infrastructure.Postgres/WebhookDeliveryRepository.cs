using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Insert-only, matching the port's own remarks.
///
/// `6-05`: catches the unique-index violation on <c>(endpoint_id, message_id)</c>
/// (`WebhookDeliveryConfiguration`) exactly the way
/// `Ago.Platform.Persistence.Postgres.EfInboxChecker.IsUniqueViolation` catches its own - a
/// redelivered event racing this same insert loses cleanly instead of throwing an unhandled
/// exception back into the consumer, which would otherwise turn a harmless duplicate into a poison
/// message retried into the DLQ for no reason.</summary>
public sealed class WebhookDeliveryRepository(AgoChatDbContext db) : IWebhookDeliveryRepository
{
    public async Task<bool> SaveAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        if (db.Entry(delivery).State == EntityState.Detached)
        {
            db.WebhookDeliveries.Add(delivery);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The insert never landed - detach so a caller reusing this same DbContext instance for
            // further work does not keep tracking a phantom row EF still believes is pending.
            db.Entry(delivery).State = EntityState.Detached;
            return false;
        }
    }

    public Task<bool> ExistsAsync(WebhookEndpointId endpointId, Guid messageId, CancellationToken cancellationToken) =>
        db.WebhookDeliveries.AsNoTracking().AnyAsync(d => d.EndpointId == endpointId && d.MessageId == messageId, cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
