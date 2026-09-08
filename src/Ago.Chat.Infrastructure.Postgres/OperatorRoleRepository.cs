using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

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
}
