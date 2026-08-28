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
}
