using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class VisitorRepository(AgoChatDbContext db) : IVisitorRepository
{
    public Task<Visitor?> GetByIdAsync(VisitorId id, CancellationToken cancellationToken) =>
        db.Visitors.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    // `25-56`: GetOperatorQueueHandler's own batch read - see IVisitorRepository's own remarks for why
    // this is one query over the distinct ids across both its lists rather than a loop of
    // GetByIdAsync. AsNoTracking: this handler only ever reads EmojiCreature/EmojiFood off the result,
    // never saves it back, the same read-only shape every other AsNoTracking query in this project uses.
    public async Task<IReadOnlyDictionary<VisitorId, Visitor>> GetManyByIdsAsync(
        IReadOnlyCollection<VisitorId> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<VisitorId, Visitor>();
        }

        var visitors = await db.Visitors.AsNoTracking().Where(v => ids.Contains(v.Id)).ToListAsync(cancellationToken);
        return visitors.ToDictionary(v => v.Id);
    }

    public async Task SaveAsync(Visitor visitor, CancellationToken cancellationToken)
    {
        if (db.Entry(visitor).State == EntityState.Detached)
        {
            db.Visitors.Add(visitor);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
