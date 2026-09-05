using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetAcceptancesForSubject;

public sealed record GetAcceptancesForSubject(AcceptanceSubjectKind SubjectKind, Guid SubjectId);
