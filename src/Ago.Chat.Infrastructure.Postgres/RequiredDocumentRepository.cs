using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`24-03`. The concrete read behind <see cref="IRequiredDocumentRepository"/> - a single
/// filtered query against <see cref="RequiredDocumentRecord"/>, the same shape
/// <see cref="AcceptanceRepository.GetForSubjectAsync"/> already uses for an identically small,
/// unbounded read.</summary>
public sealed class RequiredDocumentRepository(AgoChatDbContext db) : IRequiredDocumentRepository
{
    public async Task<IReadOnlyList<string>> GetRequiredDocumentKeysAsync(
        AcceptanceSubjectKind subjectKind, CancellationToken cancellationToken) =>
        await db.RequiredDocuments
            .Where(r => r.SubjectKind == subjectKind)
            .Select(r => r.DocumentKey)
            .ToListAsync(cancellationToken);
}
