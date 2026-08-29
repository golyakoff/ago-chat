using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class OperatorRepository(AgoChatDbContext db) : IOperatorRepository
{
    /// <summary>`13-07`: the composite `(external_subject_id, site_id)` index this item adds
    /// (`OperatorConfiguration`) is what makes this an equality lookup on both columns, not a scan -
    /// the same index Postgres already used for the single-column lookup this replaces.
    ///
    /// <para>`13-03`: <c>HoldsSeat &amp;&amp; RemovedAt == null</c> added - the port's own remarks on
    /// why this is the whole sign-in-blocking mechanism.</para></summary>
    public Task<Operator?> GetByExternalSubjectIdAndSiteIdAsync(string externalSubjectId, SiteId siteId, CancellationToken cancellationToken) =>
        db.Operators.FirstOrDefaultAsync(
            o => o.ExternalSubjectId == externalSubjectId && o.SiteId == siteId && o.HoldsSeat && o.RemovedAt == null,
            cancellationToken);

    /// <summary>`13-07`: every row for this identity - before this item at most one could ever exist;
    /// the composite unique index (`OperatorConfiguration`) is what makes more than one possible.
    /// `13-03`: filtered to only rows this identity may still sign in with - the port's own
    /// remarks.</summary>
    public async Task<IReadOnlyList<Operator>> ListByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken) =>
        await db.Operators
            .Where(o => o.ExternalSubjectId == externalSubjectId && o.HoldsSeat && o.RemovedAt == null)
            .ToListAsync(cancellationToken);

    /// <summary>`14-04`: <c>AsNoTracking</c> and an <c>EXISTS</c>, not a load - the caller wants a
    /// yes/no and must not accidentally be handed operator aggregates it could then mutate. No
    /// <c>active_chats</c> term, deliberately: see the port's own remarks on why this is weaker than
    /// the assignment engine's candidate query.</summary>
    public Task<bool> AnyOnlineForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        db.Operators.AsNoTracking()
            .AnyAsync(o => o.SiteId == siteId && o.Status == OperatorStatus.Online, cancellationToken);

    /// <summary>`4-06`: tracked, deliberately - the caller loads this to mutate
    /// <see cref="Operator.Status"/> via <see cref="Operator.GoOnline"/>/<see cref="Operator.GoOffline"/>
    /// and then calls <see cref="SaveAsync"/>, so an <c>AsNoTracking</c> read here would silently make
    /// that save a no-op.</summary>
    public Task<Operator?> GetByIdAsync(OperatorId id, CancellationToken cancellationToken) =>
        db.Operators.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    /// <summary>`13-03`: the same tracked lookup, scoped to a site - see the port's own remarks on why
    /// the miss case (wrong site or no such id) is deliberately indistinguishable.</summary>
    public Task<Operator?> GetByIdAsync(OperatorId id, SiteId siteId, CancellationToken cancellationToken) =>
        db.Operators.FirstOrDefaultAsync(o => o.Id == id && o.SiteId == siteId, cancellationToken);

    /// <summary>`13-03`: <c>AsNoTracking</c>, the same "read-only yes/no/count question" shape
    /// <see cref="AnyOnlineForSiteAsync"/> already establishes.</summary>
    public Task<int> CountHeldSeatsAsync(SiteId siteId, CancellationToken cancellationToken) =>
        db.Operators.AsNoTracking()
            .CountAsync(o => o.SiteId == siteId && o.HoldsSeat && o.RemovedAt == null, cancellationToken);

    /// <summary>No `EntityState.Detached` branch (contrast `ConversationRepository.SaveAsync`) - every
    /// caller of this port loads the operator through a `GetByIdAsync` overload first, so it is always
    /// already tracked; SaveChangesAsync alone picks up the mutation. No concurrency-conflict translation
    /// either - `OperatorConfiguration` gives this table no concurrency token, unlike `conversations`.</summary>
    public Task SaveAsync(Operator operatorEntity, CancellationToken cancellationToken) =>
        db.SaveChangesAsync(cancellationToken);
}
