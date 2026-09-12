using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`24-03`/`24-16`. The concrete read/write behind <see cref="IRequiredDocumentRepository"/> -
/// a filtered query against <see cref="RequiredDocumentRecord"/>, the same shape
/// <see cref="AcceptanceRepository.GetForSubjectAsync"/> already uses for an identically small,
/// unbounded read, plus the two writes `24-16` adds.</summary>
public sealed class RequiredDocumentRepository(AgoChatDbContext db, IIdGenerator idGenerator, IClock clock)
    : IRequiredDocumentRepository
{
    public async Task<IReadOnlyList<string>> GetRequiredDocumentKeysAsync(
        AcceptanceSubjectKind subjectKind, CancellationToken cancellationToken) =>
        await db.RequiredDocuments
            .Where(r => r.SubjectKind == subjectKind)
            .Select(r => r.DocumentKey)
            .ToListAsync(cancellationToken);

    /// <summary>Checked first, before any write is even staged - a plain read rather than relying on
    /// the unique index alone, so the ordinary "not already required" path never has to pay for an
    /// exception. The unique-violation catch below is the correctness guarantee for the genuine race
    /// (two owner calls for the identical pair landing together); this check is only the fast, common
    /// path, the same split <c>SiteRegistrationRepository.TryRegisterAsync</c>'s own remarks describe
    /// for its own now-rare race.</summary>
    public async Task<bool> AddAsync(AcceptanceSubjectKind subjectKind, string documentKey, CancellationToken cancellationToken)
    {
        var alreadyRequired = await db.RequiredDocuments
            .AnyAsync(r => r.SubjectKind == subjectKind && r.DocumentKey == documentKey, cancellationToken);
        if (alreadyRequired)
        {
            return false;
        }

        var now = clock.UtcNow;
        db.RequiredDocuments.Add(new RequiredDocumentRecord
        {
            Id = idGenerator.NewId(now),
            SubjectKind = subjectKind,
            DocumentKey = documentKey,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a race against a concurrent AddAsync for the identical pair - the pair is required
            // either way, so this is not this caller's failure to report. Detached so a caller reusing
            // this same DbContext instance for further work does not keep tracking a phantom row EF
            // still believes is pending, the identical cleanup
            // SiteRegistrationRepository.TryRegisterAsync's own remarks give for its own conflict.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }

    /// <summary>`ExecuteDeleteAsync` rather than load-then-remove through change tracking - the same
    /// bulk-delete shape <c>OperatorRoleRepository</c>'s own remarks already use for an identically
    /// bare membership row with no aggregate behind it. Translates directly to one SQL `DELETE ... WHERE
    /// subject_kind = ... AND document_key = ...` against `required_documents` alone - the guarantee
    /// this method's own port remarks make structural: there is no join, no cascade, and nothing in
    /// this statement that could reach `acceptance_records`.</summary>
    public async Task<bool> RemoveAsync(AcceptanceSubjectKind subjectKind, string documentKey, CancellationToken cancellationToken)
    {
        var deleted = await db.RequiredDocuments
            .Where(r => r.SubjectKind == subjectKind && r.DocumentKey == documentKey)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
