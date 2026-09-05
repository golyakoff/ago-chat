using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.UseCases.GetAcceptancesForSubject;

/// <summary>
/// `24-01`'s own "read back" half of Done-when #1. Deliberately unauthenticated at this layer - no
/// permission check, no host endpoint in this item at all (Scope: "showing anything to anybody" is
/// `24-03`/`24-04`/`24-05`'s job, not this one's). This handler exists so the round trip - record,
/// then read back subject, document, version and timestamp - is provable without a database open in
/// front of a person, and so those later items have a use case to call rather than a raw repository.
/// </summary>
public sealed class GetAcceptancesForSubjectHandler(IAcceptanceRepository acceptances)
{
    public async Task<IReadOnlyList<AcceptanceRecordDto>> HandleAsync(
        GetAcceptancesForSubject query, CancellationToken cancellationToken)
    {
        var records = await acceptances.GetForSubjectAsync(query.SubjectKind, query.SubjectId, cancellationToken);

        return records
            .Select(r => new AcceptanceRecordDto(
                r.Id.Value, r.SubjectKind, r.SubjectId, r.DocumentKey, r.DocumentVersion, r.AcceptedAt, r.ClientIp, r.UserAgent))
            .ToList();
    }
}
