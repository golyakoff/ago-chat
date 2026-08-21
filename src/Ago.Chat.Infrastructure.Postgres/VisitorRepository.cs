using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class VisitorRepository(AgoChatDbContext db) : IVisitorRepository
{
    public Task<Visitor?> GetByIdAsync(VisitorId id, CancellationToken cancellationToken) =>
        db.Visitors.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public async Task SaveAsync(Visitor visitor, CancellationToken cancellationToken)
    {
        if (db.Entry(visitor).State == EntityState.Detached)
        {
            db.Visitors.Add(visitor);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
