using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `24-02`: the port for <see cref="Document"/>/<see cref="PublishedDocumentVersion"/> - declared here,
/// implemented in `Ago.Chat.Infrastructure.Postgres` (clean-architecture.md's dependency rule).
///
/// <para><b>Three read shapes, not one generic query</b> - the same "shaped by the use cases that
/// actually need it" reasoning <see cref="IAcceptanceRepository"/>'s own remarks give. The one loaded
/// through <see cref="Document"/> itself (<see cref="GetByKeyAsync"/>) is for the single write path
/// (<c>PublishDocumentVersionHandler</c>, which needs the whole aggregate to call
/// <see cref="Document.Publish"/> against its own <see cref="Document.LastSequence"/>). The two direct
/// ones (<see cref="FindVersionAsync"/>/<see cref="FindCurrentAsync"/>) are for the public,
/// unauthenticated, cached read path (<c>GetDocumentVersionHandler</c>) - they query
/// <see cref="PublishedDocumentVersion"/> straight through its own denormalised
/// <see cref="PublishedDocumentVersion.DocumentKey"/>, never loading the parent <see cref="Document"/>
/// or its full <see cref="Document.Versions"/> list just to answer "what does version 4 say" for a
/// caller with no reason to see anything else.</para>
/// </summary>
public interface IDocumentRepository
{
    Task<Document?> GetByKeyAsync(string documentKey, CancellationToken cancellationToken);

    /// <summary>
    /// Persists <paramref name="document"/> - a brand-new <see cref="Document"/> (never saved before)
    /// is inserted together with every version in <see cref="Document.Versions"/>; one already loaded
    /// through <see cref="GetByKeyAsync"/> has only its newly appended version inserted, guarded by the
    /// row's own optimistic-concurrency token. Throws <see cref="DocumentConcurrencyConflictException"/>
    /// - never <c>Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException</c> - if another publish
    /// for the same key committed first; see that type's own remarks for why the translation happens
    /// at this port boundary.
    /// </summary>
    Task SaveAsync(Document document, CancellationToken cancellationToken);

    /// <summary>A specific, already-published version - <see langword="null"/> if no version with that
    /// exact string was ever published under that key. Immutable once it exists (`24-02`'s own
    /// invariant), so a caller may cache a hit against this method far more aggressively than a
    /// <see cref="FindCurrentAsync"/> hit.</summary>
    Task<PublishedDocumentVersion?> FindVersionAsync(string documentKey, string version, CancellationToken cancellationToken);

    /// <summary>The document's own current (most recently published) version - <see langword="null"/>
    /// if nothing has ever been published under that key at all (an unknown document key, or a real
    /// one this deployment has not yet published anything for).</summary>
    Task<PublishedDocumentVersion?> FindCurrentAsync(string documentKey, CancellationToken cancellationToken);
}
