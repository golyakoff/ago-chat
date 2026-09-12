using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AddRequiredDocument;

/// <summary>
/// `24-16`: "a subject of <see cref="SubjectKind"/> must accept <see cref="DocumentKey"/>" - the
/// platform owner's own write, closing the gap `24-03`'s own port docstring named: "a lawyer's later
/// verdict... would again cost a release rather than a row." Deliberately carries no check that
/// <see cref="DocumentKey"/> names anything already published - `RegisterSiteHandler`'s own remarks
/// already establish that ordering is legal ("the owner declared this key required... before publishing
/// anything under it"), so this command must not re-forbid what that handler already accepts.
/// </summary>
public sealed record AddRequiredDocument(AcceptanceSubjectKind SubjectKind, string DocumentKey);

/// <summary><see cref="AlreadyRequired"/> is <see langword="true"/> when this exact pair was already
/// required before this call - not an error (see <see cref="Abstractions.IRequiredDocumentRepository.AddAsync"/>'s
/// own remarks on why this write is idempotent), but worth telling the caller, since a platform owner
/// who just clicked "add" twice may want to know the second click did nothing new.</summary>
public sealed record AddedRequiredDocument(AcceptanceSubjectKind SubjectKind, string DocumentKey, bool AlreadyRequired);
