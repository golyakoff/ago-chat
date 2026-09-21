using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `26-03`/`adr/0179` §1: the third of the three revocation causes named there - "the operator was
/// removed from the site." <see cref="OperatorRemovedConsumer"/>'s own added call, constructed the
/// identical "instantiate <see cref="OperatorDeviceRepository"/> directly, sharing this batch's own db"
/// shape <see cref="OperatorConversationReleaser"/>'s own remarks describe for
/// <c>OperatorCapacityStore</c> - a small, stateless-beyond-the-shared-pool class registered the same
/// singleton way, rather than folded into <see cref="OperatorConversationReleaser"/> itself: that type's
/// own name and doc comment are about releasing <em>conversations</em>, and conflating "revoke this
/// operator's devices" into it would make a reader of its one Done-when have to also reason about push
/// devices to understand what it does. One Postgres transaction per call - not required for
/// correctness (no idempotency ledger backs either write, `adr/0020`, so a redelivered
/// `OperatorRemovedFromSite` simply revokes an already-revoked row a second time, harmlessly), but free
/// given the identical shared-connection construction this codebase already uses everywhere else in
/// this file.
/// </summary>
public sealed class OperatorDeviceRevoker(NpgsqlDataSource dataSource, IClock clock)
{
    public async Task<int> RevokeAllAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connection).Options;
        await using var db = new AgoChatDbContext(dbOptions);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var devices = new OperatorDeviceRepository(db);
        var now = clock.UtcNow;

        var active = await devices.ListActiveForOperatorAsync(operatorId, cancellationToken);
        foreach (var device in active)
        {
            device.Revoke(now);
            await devices.SaveAsync(device, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return active.Count;
    }
}
