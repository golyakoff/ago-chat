using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetAcceptancesForSubject;

public sealed record AcceptanceRecordDto(
    Guid Id,
    AcceptanceSubjectKind SubjectKind,
    Guid SubjectId,
    string DocumentKey,
    string DocumentVersion,
    DateTimeOffset AcceptedAt,
    string? ClientIp,
    string? UserAgent);
