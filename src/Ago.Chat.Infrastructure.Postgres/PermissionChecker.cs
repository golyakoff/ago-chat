using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

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
}
