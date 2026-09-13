using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `22-08`: writes through the caller's own scoped <see cref="AgoChatDbContext"/> via
/// <c>ExecuteSqlInterpolatedAsync</c>, the identical "join the ambient transaction, never a fresh
/// connection" shape <see cref="RoleChangeRecordRepository"/> already establishes for its own analogous
/// audit row - <see cref="ISiteSuspensionRecordRepository"/>'s own remarks state why: this record must
/// commit or roll back atomically with the <c>Site</c> write it documents, on the same connection and
/// transaction every other write in the handler's own <c>IUnitOfWork</c>-scoped block shares.
/// </summary>
public sealed class SiteSuspensionRecordRepository(AgoChatDbContext db) : ISiteSuspensionRecordRepository
{
    public Task RecordAsync(SiteSuspensionRecordToWrite record, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            insert into site_suspensions
                (id, site_id, action, performed_by, reason, suspended_until, performed_at)
            values
                ({record.Id}, {record.SiteId.Value}, {record.Action}, {record.PerformedBy}, {record.Reason},
                 {record.SuspendedUntil}, {record.PerformedAt})
            """,
            cancellationToken);
}
