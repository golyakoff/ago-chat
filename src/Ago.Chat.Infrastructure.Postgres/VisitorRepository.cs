using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    // `25-67`: translated here, not left to propagate as EF's own type - the same "the adapter is the
    // one place in the whole call chain allowed to know which ORM raised it" shape
    // ConversationRepository.SaveAsync's own remarks establish for `6-08`. Scoped to exactly
    // `PK_visitors` by name, not every unique violation this SaveChangesAsync could ever raise on this
    // shared DbContext - the same "translate exactly the constraint this call site can explain,
    // nothing wider" precedent ConversationRepository's own three catches already set (a genuine
    // future unique constraint on this table would mean something else entirely and must not be
    // folded into this one). Unlike ConversationRepository, there is no DbUpdateConcurrencyException
    // clause alongside it: VisitorConfiguration configures no `xmin`/`IsRowVersion` for this entity, so
    // a returning visitor's Touch()+SaveAsync is a plain by-id UPDATE with nothing to lose a
    // compare-and-set against. The only way SaveChangesAsync can fail here at all is two callers each
    // constructing their own brand-new Visitor for the same VisitorId and both INSERTing - the loser's
    // own row hitting the primary key an instant after the winner's, never a stale UPDATE racing a row
    // someone else already changed.
    public async Task SaveAsync(Visitor visitor, CancellationToken cancellationToken)
    {
        if (db.Entry(visitor).State == EntityState.Detached)
        {
            db.Visitors.Add(visitor);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_visitors",
        })
        {
            // Same reasoning as ConversationRepository.SaveAsync's own Clear() calls: a caller's retry
            // must re-read the truth from Postgres, not the identity map's stale copy of a row whose
            // insert never actually committed.
            db.ChangeTracker.Clear();
            throw new VisitorConcurrencyConflictException(visitor.Id);
        }
    }
}
