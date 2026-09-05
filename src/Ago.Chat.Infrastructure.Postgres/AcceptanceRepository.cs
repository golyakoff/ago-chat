using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `24-01`. The concrete expression of <see cref="IAcceptanceRepository"/>'s own "no delete method"
/// remarks - this class has no method that could remove a row, only <see cref="SaveAsync"/> (always
/// an insert - <see cref="AcceptanceRecord"/> has no `Rename`/`Update`-shaped domain method for it to
/// call instead) and a read.
/// </summary>
public sealed class AcceptanceRepository(AgoChatDbContext db) : IAcceptanceRepository
{
    public async Task SaveAsync(AcceptanceRecord record, CancellationToken cancellationToken)
    {
        db.AcceptanceRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AcceptanceRecord>> GetForSubjectAsync(
        AcceptanceSubjectKind subjectKind, Guid subjectId, CancellationToken cancellationToken) =>
        await db.AcceptanceRecords
            .Where(a => a.SubjectKind == subjectKind && a.SubjectId == subjectId)
            .OrderBy(a => a.AcceptedAt)
            .ToListAsync(cancellationToken);
}
