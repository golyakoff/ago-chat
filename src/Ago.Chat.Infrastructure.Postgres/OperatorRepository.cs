using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class OperatorRepository(AgoChatDbContext db) : IOperatorRepository
{
    public Task<Operator?> GetByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken) =>
        db.Operators.FirstOrDefaultAsync(o => o.ExternalSubjectId == externalSubjectId, cancellationToken);
}
