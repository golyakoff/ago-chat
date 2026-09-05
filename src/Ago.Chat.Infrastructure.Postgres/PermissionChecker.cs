using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class PermissionChecker(AgoChatDbContext db) : IPermissionChecker
{
    public Task<bool> HasPermissionAsync(
        OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken)
    {
        var roleIds = db.OperatorRoles
            .Where(or => or.OperatorId == operatorId)
            .Select(or => or.RoleId);

        return db.Roles
            .Where(r => r.SiteId == siteId && roleIds.Contains(r.Id))
            .AnyAsync(r => r.Permissions.Contains(permission.Value), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken)
    {
        var roleIds = db.OperatorRoles
            .Where(or => or.OperatorId == operatorId)
            .Select(or => or.RoleId);

        var permissions = await db.Roles
            .Where(r => r.SiteId == siteId && roleIds.Contains(r.Id))
            .SelectMany(r => r.Permissions)
            .Distinct()
            .ToListAsync(cancellationToken);

        return permissions;
    }

    /// <summary>`23-26`: see the port's own remarks - locks `sites` (`FOR UPDATE`) through this same
    /// <see cref="AgoChatDbContext"/>'s ambient transaction before counting, the identical
    /// raw-Npgsql-inside-an-EF-transaction shape <c>OperatorInviteRedemptionRepository.LockSiteAndReadSeatLimitAsync</c>
    /// already established for the same reason: EF has no LINQ shape for `FOR UPDATE`, and the lock
    /// only serializes concurrent callers if it is taken on the same connection and transaction the
    /// eventual write commits on.</summary>
    public async Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken)
    {
        await LockSiteAsync(siteId, cancellationToken);

        var roleIds = db.Roles
            .Where(r => r.SiteId == siteId && r.Permissions.Contains(permission.Value))
            .Select(r => r.Id);

        return await db.Operators
            .Where(o => o.SiteId == siteId && o.RemovedAt == null)
            .Where(o => db.OperatorRoles.Any(or => or.OperatorId == o.Id && roleIds.Contains(or.RoleId)))
            .CountAsync(cancellationToken);
    }

    private async Task LockSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        await using var command = new NpgsqlCommand("SELECT id FROM sites WHERE id = @siteId FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            // A foreign key (RoleRecordConfiguration.HasOne<Site>) should make this unreachable - the
            // same "should have prevented this" throw OperatorInviteRedemptionRepository's own
            // LockSiteAndReadSeatLimitAsync raises for the identical impossible case.
            throw new InvalidOperationException(
                $"Site {siteId.Value} was not found while counting operators who manage it - a foreign key should have prevented this.");
        }
    }
}
