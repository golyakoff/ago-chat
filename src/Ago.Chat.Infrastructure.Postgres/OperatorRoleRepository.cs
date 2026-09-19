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
///
/// <para><b>`25-170`: also the seat-holding half of this same join table</b> - see
/// <see cref="IOperatorRoleRepository"/>'s own remarks for the full reasoning.</para>
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
    /// <see cref="OperatorRoleRecordConfiguration"/>) and nothing here needs the deleted rows back beyond
    /// the `HoldsSeat` value already read below, the same bulk-statement shape this codebase already
    /// reaches for when a delete has no aggregate invariant to enforce. Participates in the caller's own
    /// ambient transaction automatically - EF's bulk `Execute*Async` family joins
    /// `Database.CurrentTransaction` the same way `SaveChangesAsync` does (`EfUnitOfWork`'s own remarks),
    /// so this and the insert below commit or roll back together with whatever else the caller staged on
    /// this same <see cref="AgoChatDbContext"/>.
    /// </summary>
    public async Task ReplaceRoleAsync(OperatorId operatorId, Guid roleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // `25-170`: read before the delete - "any role held a moment ago has HoldsSeat = true" decides
        // whether the freshly assigned role carries a seat forward. See IOperatorRoleRepository.
        // ReplaceRoleAsync's own remarks on why "any", not "the first", and why this is a deliberate,
        // documented judgement call for the two-roles-at-once (founder) case.
        var existingSeatFlags = await db.OperatorRoles.AsNoTracking()
            .Where(or => or.OperatorId == operatorId)
            .Select(or => or.HoldsSeat)
            .ToListAsync(cancellationToken);
        var holdsSeat = existingSeatFlags.Any(h => h);

        await db.OperatorRoles
            .Where(or => or.OperatorId == operatorId)
            .ExecuteDeleteAsync(cancellationToken);

        db.OperatorRoles.Add(new OperatorRoleRecord
        {
            OperatorId = operatorId,
            RoleId = roleId,
            HoldsSeat = holdsSeat,
            GrantedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> HoldsAnySeatAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
        db.OperatorRoles.AsNoTracking().AnyAsync(or => or.OperatorId == operatorId && or.HoldsSeat, cancellationToken);

    public Task<bool> HoldsRoleSeatAsync(OperatorId operatorId, SiteId siteId, string roleName, CancellationToken cancellationToken)
    {
        var roleIds = RoleIdsFor(siteId, roleName);
        return db.OperatorRoles.AsNoTracking()
            .AnyAsync(or => or.OperatorId == operatorId && or.HoldsSeat && roleIds.Contains(or.RoleId), cancellationToken);
    }

    public async Task<IReadOnlyList<OperatorId>> GetHeldSeatHolderIdsAsync(SiteId siteId, string roleName, CancellationToken cancellationToken) =>
        await QueryHeldSeatHolderIds(siteId, roleName).ToListAsync(cancellationToken);

    /// <summary>`25-25`/`25-170`: see the port's own remarks - locks `sites` (`FOR UPDATE`) through this
    /// same <see cref="AgoChatDbContext"/>'s ambient transaction before counting, the identical
    /// raw-Npgsql-inside-an-EF-transaction shape <c>PermissionChecker.CountNonRemovedHoldersAsync</c>
    /// already established for the same reason: EF has no LINQ shape for `FOR UPDATE`, and the lock
    /// only serializes concurrent callers if it is taken on the same connection and transaction the
    /// eventual write commits on.</summary>
    public async Task<IReadOnlyList<OperatorId>> LockAndGetHeldSeatHolderIdsAsync(
        SiteId siteId, string roleName, CancellationToken cancellationToken)
    {
        await LockSiteAsync(siteId, roleName, cancellationToken);
        return await QueryHeldSeatHolderIds(siteId, roleName).ToListAsync(cancellationToken);
    }

    public async Task SetHoldsSeatAsync(
        OperatorId operatorId, SiteId siteId, string roleName, bool holdsSeat, CancellationToken cancellationToken)
    {
        var roleIds = RoleIdsFor(siteId, roleName);
        await db.OperatorRoles
            .Where(or => or.OperatorId == operatorId && roleIds.Contains(or.RoleId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(or => or.HoldsSeat, holdsSeat), cancellationToken);
    }

    private IQueryable<Guid> RoleIdsFor(SiteId siteId, string roleName) =>
        db.Roles.Where(r => r.SiteId == siteId && r.Name == roleName).Select(r => r.Id);

    /// <summary>The shared predicate <see cref="GetHeldSeatHolderIdsAsync"/> and
    /// <see cref="LockAndGetHeldSeatHolderIdsAsync"/> both reduce to once they have each made their own
    /// decision about locking - every non-removed operator on <paramref name="siteId"/> who currently
    /// holds <paramref name="roleName"/>'s own seat, ordered most-recently-granted-first (ties broken by
    /// <see cref="OperatorId"/>) - the reconciliation procedure's own required order, harmless overhead
    /// for the plain-count callers.</summary>
    private IQueryable<OperatorId> QueryHeldSeatHolderIds(SiteId siteId, string roleName)
    {
        var roleIds = RoleIdsFor(siteId, roleName);

        return db.OperatorRoles.AsNoTracking()
            .Where(or => or.HoldsSeat && roleIds.Contains(or.RoleId))
            .Join(
                db.Operators.AsNoTracking().Where(o => o.SiteId == siteId && o.RemovedAt == null),
                or => or.OperatorId, o => o.Id, (or, o) => new { or.OperatorId, or.GrantedAt })
            .OrderByDescending(x => x.GrantedAt)
            .ThenBy(x => x.OperatorId)
            .Select(x => x.OperatorId);
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
