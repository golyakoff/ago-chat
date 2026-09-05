using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `24-03`: one row per "a subject of this kind must accept this document" - the storage shape behind
/// <see cref="Ago.Chat.Application.Abstractions.IRequiredDocumentRepository"/>. A plain persistence
/// record with no invariants of its own, the same "nothing above it manages this yet" shape
/// <see cref="RoleRecord"/>'s own remarks give for the identical reason: there is no Domain or
/// Application entity behind this table because there is no behaviour to attach to a row beyond "it
/// exists" - the requirement itself is the whole fact, and `RegisterSiteHandler`'s own remarks
/// (`Ago.Chat.Application`) describe what reading it means for registration.
/// </summary>
internal sealed class RequiredDocumentRecord
{
    public Guid Id { get; set; }
    public AcceptanceSubjectKind SubjectKind { get; set; }
    public string DocumentKey { get; set; } = string.Empty;
}
