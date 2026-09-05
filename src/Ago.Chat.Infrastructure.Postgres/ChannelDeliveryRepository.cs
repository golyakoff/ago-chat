using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`23-19`: insert-only, matching <c>WebhookDeliveryRepository</c>'s own shape exactly -
/// see <see cref="IChannelDeliveryRepository"/>'s own remarks for why.</summary>
public sealed class ChannelDeliveryRepository(AgoChatDbContext db) : IChannelDeliveryRepository
{
    public async Task<bool> SaveAsync(ChannelDelivery delivery, CancellationToken cancellationToken)
    {
        if (db.Entry(delivery).State == EntityState.Detached)
        {
            db.ChannelDeliveries.Add(delivery);
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

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
