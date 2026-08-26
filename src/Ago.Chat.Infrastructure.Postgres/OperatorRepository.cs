using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class OperatorRepository(AgoChatDbContext db) : IOperatorRepository
{
    public Task<Operator?> GetByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken) =>
        db.Operators.FirstOrDefaultAsync(o => o.ExternalSubjectId == externalSubjectId, cancellationToken);

    /// <summary>`14-04`: <c>AsNoTracking</c> and an <c>EXISTS</c>, not a load - the caller wants a
    /// yes/no and must not accidentally be handed operator aggregates it could then mutate. No
    /// <c>active_chats</c> term, deliberately: see the port's own remarks on why this is weaker than
    /// the assignment engine's candidate query.</summary>
    public Task<bool> AnyOnlineForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        db.Operators.AsNoTracking()
            .AnyAsync(o => o.SiteId == siteId && o.Status == OperatorStatus.Online, cancellationToken);
}
