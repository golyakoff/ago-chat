using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-72`: plain EF over the same `operator_roles` table <see cref="SiteRegistrationRepository"/> and
/// <see cref="OperatorInviteRedemptionRepository"/> already write to directly - this is the third writer,
/// and the first one that changes an *existing* row's assignment rather than only ever inserting.
/// </summary>
public sealed class OperatorRoleRepository(AgoChatDbContext db) : IOperatorRoleRepository
{
    public async Task<IReadOnlyList<string>> GetRoleNamesAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        var names = await db.OperatorRoles.AsNoTracking()
            .Where(or => or.OperatorId == operatorId)
            .Join(db.Roles.AsNoTracking(), or => or.RoleId, r => r.Id, (or, r) => r.Name)
            .ToListAsync(cancellationToken);
        return names;
    }

    /// <summary>
    /// `ExecuteDeleteAsync` rather than load-then-remove through change tracking - `operator_roles` has
    /// no independent id to track by (its key is the `(OperatorId, RoleId)` pair,
    /// <see cref="OperatorRoleRecordConfiguration"/>) and nothing here needs the deleted rows back, the
    /// same bulk-statement shape this codebase already reaches for when a delete has no aggregate
    /// invariant to enforce. Participates in the caller's own ambient transaction automatically - EF's
    /// bulk `Execute*Async` family joins `Database.CurrentTransaction` the same way `SaveChangesAsync`
    /// does (`EfUnitOfWork`'s own remarks), so this and the insert below commit or roll back together
    /// with whatever else the caller staged on this same <see cref="AgoChatDbContext"/>.
    /// </summary>
    public async Task ReplaceRoleAsync(OperatorId operatorId, Guid roleId, CancellationToken cancellationToken)
    {
        await db.OperatorRoles
            .Where(or => or.OperatorId == operatorId)
            .ExecuteDeleteAsync(cancellationToken);

        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>`25-25`: see the port's own remarks - locks `sites` (`FOR UPDATE`) through this same
    /// <see cref="AgoChatDbContext"/>'s ambient transaction before counting, the identical
    /// raw-Npgsql-inside-an-EF-transaction shape <c>PermissionChecker.CountNonRemovedHoldersAsync</c>
    /// already established for the same reason: EF has no LINQ shape for `FOR UPDATE`, and the lock
    /// only serializes concurrent callers if it is taken on the same connection and transaction the
    /// eventual write commits on.</summary>
    public async Task<int> CountNonRemovedHoldersAsync(SiteId siteId, string roleName, CancellationToken cancellationToken)
    {
        await LockSiteAsync(siteId, roleName, cancellationToken);

        var roleIds = db.Roles
            .Where(r => r.SiteId == siteId && r.Name == roleName)
            .Select(r => r.Id);

        return await db.Operators
            .Where(o => o.SiteId == siteId && o.RemovedAt == null)
            .Where(o => db.OperatorRoles.Any(or => or.OperatorId == o.Id && roleIds.Contains(or.RoleId)))
            .CountAsync(cancellationToken);
    }

    /// <summary>`25-41`: the identical predicate <see cref="CountNonRemovedHoldersAsync"/> uses,
    /// returning the ids instead of only a count - no lock, unlike that method's own row-locked
    /// count, because `AdministratorLimitEnforcer`'s own caller (`Site.ActivateSubscription`'s own
    /// caller) already holds the relevant lock on this same site row by the time this runs.</summary>
    public async Task<IReadOnlyList<OperatorId>> GetNonRemovedHolderIdsAsync(
        SiteId siteId, string roleName, CancellationToken cancellationToken)
    {
        var roleIds = db.Roles
            .Where(r => r.SiteId == siteId && r.Name == roleName)
            .Select(r => r.Id);

        return await db.Operators
            .Where(o => o.SiteId == siteId && o.RemovedAt == null)
            .Where(o => db.OperatorRoles.Any(or => or.OperatorId == o.Id && roleIds.Contains(or.RoleId)))
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task LockSiteAsync(SiteId siteId, string roleName, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        await using var command = new NpgsqlCommand("SELECT id FROM sites WHERE id = @siteId FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            // A foreign key (RoleRecordConfiguration.HasOne<Site>) should make this unreachable - the
            // same "should have prevented this" throw PermissionChecker.LockSiteAsync's own remarks
            // raise for the identical impossible case.
            throw new InvalidOperationException(
                $"Site {siteId.Value} was not found while counting operators who hold role '{roleName}' - " +
                "a foreign key should have prevented this.");
        }
    }
}
