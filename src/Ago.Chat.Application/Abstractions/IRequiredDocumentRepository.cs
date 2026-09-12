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
/// <para><b>`24-16`: no longer read-only.</b> `24-03` deliberately left this port with no write method -
/// "a row is added today by a migration or a direct write; a dedicated owner-facing endpoint is a
/// future, separate item's job." This item is that future item: <see cref="AddAsync"/> and
/// <see cref="RemoveAsync"/> are the whole mechanism a platform owner needs, added to this same port
/// rather than a second one, the identical "one port per table, reads and writes together" shape
/// <see cref="IAcceptanceRepository"/> and `IDocumentRepository` already use for their own tables.</para>
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

    /// <summary>`24-16`: declares that a subject of <paramref name="subjectKind"/> must accept
    /// <paramref name="documentKey"/> - the platform owner's own write, and `24-03`'s own port docstring
    /// come true: "add a row, and a subject kind newly requires a document with no code touched."
    /// Idempotent - a pair that is already required is not an error, and this returns
    /// <see langword="false"/> rather than throwing, the same "second identical write is safe" posture
    /// <c>RoleRepository.AddPermissionsAsync</c>'s own dedup already gives an almost identical
    /// set-membership fact. Returns <see langword="true"/> only when a new row was actually
    /// written.</summary>
    Task<bool> AddAsync(AcceptanceSubjectKind subjectKind, string documentKey, CancellationToken cancellationToken);

    /// <summary>`24-16`: withdraws the requirement. <b>Never touches <c>acceptance_records</c></b> - see
    /// `adr/0111` and <see cref="AcceptanceRecord"/>'s own erasure remarks: an acceptance already
    /// recorded against <paramref name="documentKey"/> is written by a wholly separate write
    /// (<see cref="IAcceptanceRepository.SaveAsync"/>), through a wholly separate table with no foreign
    /// key back to this one (`required_documents`' own only index is the composite
    /// `(subject_kind, document_key)` uniqueness constraint - nothing else), so a row removed here
    /// cannot cascade into one there structurally, not merely by this method choosing not to. A tenant
    /// who accepted v3 accepted it, whether or not v3 (or the requirement itself) still exists.
    /// Idempotent - removing a pair that was not required is not an error, and this returns
    /// <see langword="false"/> rather than throwing. Returns <see langword="true"/> only when a row
    /// actually existed to remove.</summary>
    Task<bool> RemoveAsync(AcceptanceSubjectKind subjectKind, string documentKey, CancellationToken cancellationToken);
}
