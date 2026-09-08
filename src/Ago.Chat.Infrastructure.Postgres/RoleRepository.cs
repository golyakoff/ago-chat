using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class RoleRepository(AgoChatDbContext db) : IRoleRepository
{
    public async Task<Guid?> GetIdByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken)
    {
        var role = await db.Roles.AsNoTracking()
            .Where(r => r.SiteId == siteId && r.Name == name)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return role;
    }

    /// <summary>
    /// `23-102`: one `UPDATE` doing its own set-union, not a tracked read-modify-write - the same
    /// "compare-and-set inside the database, never in application code" reasoning
    /// `OperatorCapacityStore`/`TagRepository.AddToConversationAsync` already give their own idempotent
    /// writes. `array_agg(DISTINCT ...)` over `permissions || @new` is what makes a repeated call (a
    /// re-grant, a revoke-then-re-grant cycle) add nothing the second time, with no read beforehand and
    /// no race between two concurrent grants naming the same role - Postgres's own row-level lock on the
    /// `UPDATE` serialises them, so there is no window for a "read old value, union, write" pair to lose
    /// one caller's addition to the other's, the failure mode a tracked EF round-trip would have.
    /// </summary>
    public async Task AddPermissionsAsync(
        SiteId siteId, string roleName, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken)
    {
        if (permissions.Count == 0)
        {
            // Nothing to add - the identical "empty means nothing to do" shape
            // IModulePermissionsProvider.Get returns for a module the deployment declared no extra
            // permissions for. Skipped before ever reaching the database, not sent as a no-op UPDATE.
            return;
        }

        var newPermissions = permissions.ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            update roles
            set permissions = (select array_agg(distinct p) from unnest(permissions || {newPermissions}) as p)
            where site_id = {siteId.Value} and name = {roleName}
            """,
            cancellationToken);
    }
}
