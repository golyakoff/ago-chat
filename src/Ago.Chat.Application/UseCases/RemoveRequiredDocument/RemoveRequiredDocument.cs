using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RemoveRequiredDocument;

/// <summary>
/// `24-16`: withdraws the requirement that a subject of <see cref="SubjectKind"/> accept
/// <see cref="DocumentKey"/>. Carries no reference to any <c>AcceptanceRecord</c> - it cannot, this
/// command does not name one - which is `adr/0111`'s own guarantee made visible at the command's own
/// shape: there is nothing this command could pass down even if a future author wanted to touch
/// acceptances from here.
/// </summary>
public sealed record RemoveRequiredDocument(AcceptanceSubjectKind SubjectKind, string DocumentKey);

/// <summary><see cref="WasRequired"/> is <see langword="false"/> when this exact pair was not required
/// to begin with - not an error (see <see cref="Abstractions.IRequiredDocumentRepository.RemoveAsync"/>'s
/// own remarks on why this write is idempotent), but worth telling the caller, since a platform owner
/// removing an entry that is already gone may want to know nothing changed.</summary>
public sealed record RemovedRequiredDocument(AcceptanceSubjectKind SubjectKind, string DocumentKey, bool WasRequired);
