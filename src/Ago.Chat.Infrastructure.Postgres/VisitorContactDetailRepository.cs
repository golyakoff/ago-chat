using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `14-14`. Resolved by `RecordVisitorContactDetailHandler`/`ListVisitorContactDetailsHandler`/
/// `DeleteVisitorContactDetailHandler`/`EditVisitorContactDetailHandler`/
/// `SetVisitorContactDetailAssessmentHandler` only - no other handler in this codebase depends on
/// `IVisitorContactDetailRepository`, the concrete expression of that interface's own "structurally
/// incapable" remarks: this class shares no base type, no method, and no SQL with
/// <see cref="ChannelIdentityRepository"/>.
/// </summary>
public sealed class VisitorContactDetailRepository(AgoChatDbContext db) : IVisitorContactDetailRepository
{
    public async Task SaveAsync(VisitorContactDetail detail, CancellationToken cancellationToken)
    {
        // `25-58`: the same detached-vs-tracked branch WebhookEndpointRepository.SaveAsync already
        // uses for itself - a freshly Record()'d/RecordFromVisitor()'d detail was never loaded through
        // this context (Detached), while one an edit or assessment handler mutated in place after its
        // own GetByIdAsync is already tracked, so SaveChangesAsync alone picks up the change with no
        // explicit Update() call needed.
        if (db.Entry(detail).State == EntityState.Detached)
        {
            db.VisitorContactDetails.Add(detail);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VisitorContactDetail>> GetForVisitorAsync(
        VisitorId visitorId, CancellationToken cancellationToken) =>
        await db.VisitorContactDetails
            .Where(d => d.VisitorId == visitorId)
            .OrderBy(d => d.RecordedAt)
            .ToListAsync(cancellationToken);

    public Task<VisitorContactDetail?> GetByIdAsync(VisitorContactDetailId id, CancellationToken cancellationToken) =>
        db.VisitorContactDetails.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task DeleteAsync(VisitorContactDetail detail, CancellationToken cancellationToken)
    {
        db.VisitorContactDetails.Remove(detail);
        await db.SaveChangesAsync(cancellationToken);
    }

    // `25-56`: one round trip for every distinct visitor `GetOperatorQueueHandler` needs a name for,
    // not a `GetForVisitorAsync` call per row - the same batch shape `VisitorRepository.GetManyByIdsAsync`
    // already uses for that handler's own emoji-pair lookup. Grouped and reduced to "most recent"
    // client-side rather than in SQL: this table has no per-visitor row count worth an aggregate query
    // over (IVisitorContactDetailRepository's own remarks - "small and bounded, nobody records hundreds
    // of these per visitor"), and the queue's own two lists are already the same small/unpaginated shape
    // that justifies GetManyByIdsAsync's own single query.
    public async Task<IReadOnlyDictionary<VisitorId, string>> GetNamesForVisitorsAsync(
        IReadOnlyCollection<VisitorId> visitorIds, CancellationToken cancellationToken)
    {
        if (visitorIds.Count == 0)
        {
            return new Dictionary<VisitorId, string>();
        }

        var rows = await db.VisitorContactDetails
            .Where(d => visitorIds.Contains(d.VisitorId) && d.Kind == VisitorContactDetailKind.Name)
            .OrderByDescending(d => d.RecordedAt)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(d => d.VisitorId)
            .ToDictionary(g => g.Key, g => g.First().Value);
    }
}
