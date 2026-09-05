using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `24-03`: the port over "which documents does a subject of this kind have to accept" - declared
/// here, implemented in `Ago.Chat.Infrastructure.Postgres` (clean-architecture.md's dependency rule:
/// Application may not reference EF Core/Npgsql directly, only the abstraction over it).
///
/// <para><b>This is the whole point of `24-03`'s own backlog item, restated as a port.</b> `adr/0114`
/// put a document's <em>text</em> in Postgres so a lawyer's wording fix is a data change, never a code
/// change. That only holds end to end if <em>which</em> documents a subject must accept is data too -
/// a literal such as <c>if (subjectKind == Tenant) { ... "tenant-terms" ... }</c> inside
/// <c>RegisterSiteHandler</c> would put the requirement itself back in code, and a lawyer's later
/// verdict ("this tenant needs two documents", "an operator needs none, an employment relationship
/// already covers it") would again cost a release rather than a row. This port is what keeps that
/// verdict a row change: add a row, and a subject kind newly requires a document with no code
/// touched; remove one, and it stops requiring it, equally untouched.</para>
///
/// <para><b>Read-only, and deliberately so for this item.</b> No CRUD surface for managing rows exists
/// yet - out of this item's stated scope, which asks only that the requirement be *expressed as data
/// this item reads*, not that this item also build the platform owner's management screen for it. A
/// row is added today by a migration or a direct write; a dedicated owner-facing endpoint is a future,
/// separate item's job, the same way `24-02`'s own publish endpoint predates any owner UI for it.</para>
/// </summary>
public interface IRequiredDocumentRepository
{
    /// <summary>Every document key a subject of <paramref name="subjectKind"/> must accept, in no
    /// particular order (small and bounded - a handful of documents per subject kind, ever, the same
    /// "plain unbounded list" shape <see cref="IAcceptanceRepository.GetForSubjectAsync"/>'s own
    /// remarks already accept for an identically-bounded read). Empty - never null - when nothing is
    /// required for that kind, which is this table's default state for every subject kind today: see
    /// <c>RegisterSiteHandler</c>'s own remarks for why an empty result is a real, considered answer
    /// ("nothing beyond contract necessity today") rather than a gap.</summary>
    Task<IReadOnlyList<string>> GetRequiredDocumentKeysAsync(AcceptanceSubjectKind subjectKind, CancellationToken cancellationToken);
}
