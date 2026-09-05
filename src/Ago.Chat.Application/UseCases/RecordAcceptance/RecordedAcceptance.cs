using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RecordAcceptance;

public sealed record RecordedAcceptance(
    Guid Id,
    AcceptanceSubjectKind SubjectKind,
    Guid SubjectId,
    string DocumentKey,
    string DocumentVersion,
    DateTimeOffset AcceptedAt);
