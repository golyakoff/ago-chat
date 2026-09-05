using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.PublishDocumentVersion;

/// <summary>
/// `24-02`: the write half of the mechanism, and the one procedure by which this document's text ever
/// changes. Loads (or creates) the <see cref="Document"/> for the given key, calls
/// <see cref="Document.Publish"/> to mint the next version, and saves - retrying against a freshly
/// reloaded aggregate if <see cref="DocumentConcurrencyConflictException"/> says another publish for
/// the same key won the race, the identical shape <c>AssignConversationHandler</c>/
/// <c>CloseConversationHandler</c> already use for <see cref="Ago.Chat.Application.Abstractions.ConversationConcurrencyConflictException"/>.
///
/// <para><b>No code change, ever, for a new version, a wording fix, or a whole new document
/// replacing another.</b> This is `24-02`'s own Done-when read literally: publishing is one call to
/// this handler (in practice, one authenticated <c>POST /api/v1/owner/documents</c> the platform owner
/// makes once `ago-business` and a lawyer have signed off on the text), never an edit to a file that
/// then has to be deployed. A document that does not exist yet under a given key is created by the
/// same call that publishes its first version - there is no separate "register a document" step to
/// forget.</para>
/// </summary>
public sealed class PublishDocumentVersionHandler(IDocumentRepository documents, IIdGenerator idGenerator, IClock clock, ICache cache)
{
    // Bounded, not unbounded - this row is written by exactly one caller in practice (the platform
    // owner), so a real race is already rare; a handful of attempts is enough to ride out the
    // vanishingly unlikely case of two publishes landing in the same instant, without looping forever
    // against a genuinely broken retry.
    private const int MaxAttempts = 5;

    public async Task<Result<PublishedDocumentVersionDto>> HandleAsync(PublishDocumentVersion command, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var now = clock.UtcNow;
            var document = await documents.GetByKeyAsync(command.DocumentKey, cancellationToken);

            Document workingDocument;
            try
            {
                workingDocument = document ?? Document.Create(new DocumentId(idGenerator.NewId(now)), command.DocumentKey);
            }
            catch (ArgumentException ex)
            {
                return PublishedDocumentErrors.Invalid(ex.Message);
            }

            PublishedDocumentVersion version;
            try
            {
                version = workingDocument.Publish(
                    new PublishedDocumentVersionId(idGenerator.NewId(now)), command.Title, command.Body, now);
            }
            catch (ArgumentException ex)
            {
                return PublishedDocumentErrors.Invalid(ex.Message);
            }

            try
            {
                await documents.SaveAsync(workingDocument, cancellationToken);
            }
            catch (DocumentConcurrencyConflictException)
            {
                continue;
            }

            // `24-02`'s own read path caches a `current` hit for a bounded TTL rather than forever -
            // this eviction is what keeps a reader from having to wait out that whole window after a
            // fresh publish, the same reasoning `SiteSettingsChanged`'s own invalidation gives for
            // `SiteCacheKeys`. No eviction of `ForVersion` is needed: the version this call just created
            // was never cached before (it did not exist), so there is no stale entry to remove.
            await cache.RemoveAsync(DocumentCacheKeys.ForCurrent(workingDocument.DocumentKey), cancellationToken);

            return ToDto(version);
        }

        return PublishedDocumentErrors.PublishConflict(command.DocumentKey);
    }

    private static PublishedDocumentVersionDto ToDto(PublishedDocumentVersion version) =>
        new(version.DocumentKey, version.Version, version.Sequence, version.Title, version.Body, version.PublishedAt);
}
