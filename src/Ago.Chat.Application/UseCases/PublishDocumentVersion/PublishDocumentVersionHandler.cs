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
///
/// <para><b>`24-05`: <see cref="HandleAsSiteConsentAsync"/> joins beside <see cref="HandleAsync"/> -
/// one handler, two entry points, the identical "two differently-shaped commands, one shared core"
/// split <c>RecordVisitorContactDetailHandler</c> already establishes for itself.</b> The core publish
/// mechanics (<see cref="PublishCoreAsync"/>) are identical either way - only how the document key is
/// obtained and who is allowed to call differ, which is exactly what the two commands themselves
/// encode.</para>
/// </summary>
public sealed class PublishDocumentVersionHandler(
    IDocumentRepository documents, IPermissionChecker permissions, IIdGenerator idGenerator, IClock clock, ICache cache)
{
    // Bounded, not unbounded - this row is written by exactly one caller in practice (the platform
    // owner, or - since `24-05` - one tenant operator per site), so a real race is already rare; a
    // handful of attempts is enough to ride out the vanishingly unlikely case of two publishes landing
    // in the same instant, without looping forever against a genuinely broken retry.
    private const int MaxAttempts = 5;

    public Task<Result<PublishedDocumentVersionDto>> HandleAsync(PublishDocumentVersion command, CancellationToken cancellationToken) =>
        PublishCoreAsync(command.DocumentKey, command.Title, command.Body, cancellationToken);

    /// <summary>
    /// `24-05`: the tenant's own publish path - gated by <see cref="Permission.SiteConfigure"/>, the
    /// same permission `UpdateWidgetConfigHandler` already checks for this site, rather than
    /// `HandleAsync`'s owner-only gate. <see cref="SiteConsentDocumentKey.For"/> derives the document
    /// key from <paramref name="command"/>'s own <see cref="PublishSiteConsentDocumentVersion.SiteId"/>
    /// and <see cref="PublishSiteConsentDocumentVersion.Purpose"/> - never from a caller-supplied
    /// string - so a tenant operator can only ever publish under their own site's own consent keys,
    /// structurally, not by convention.
    /// </summary>
    public async Task<Result<PublishedDocumentVersionDto>> HandleAsSiteConsentAsync(
        PublishSiteConsentDocumentVersion command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's visitor consent document.");
        }

        if (!Enum.TryParse<VisitorConsentPurpose>(command.Purpose, ignoreCase: true, out var purpose) || !Enum.IsDefined(purpose))
        {
            return PublishedDocumentErrors.InvalidPurpose(
                $"'{command.Purpose}' is not a valid consent purpose - expected '{nameof(VisitorConsentPurpose.Contact)}' or "
                + $"'{nameof(VisitorConsentPurpose.Marketing)}'.");
        }

        var documentKey = SiteConsentDocumentKey.For(command.SiteId, purpose);
        return await PublishCoreAsync(documentKey, command.Title, command.Body, cancellationToken);
    }

    private async Task<Result<PublishedDocumentVersionDto>> PublishCoreAsync(
        string documentKey, string title, string body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var now = clock.UtcNow;
            var document = await documents.GetByKeyAsync(documentKey, cancellationToken);

            Document workingDocument;
            try
            {
                workingDocument = document ?? Document.Create(new DocumentId(idGenerator.NewId(now)), documentKey);
            }
            catch (ArgumentException ex)
            {
                return PublishedDocumentErrors.Invalid(ex.Message);
            }

            PublishedDocumentVersion version;
            try
            {
                version = workingDocument.Publish(new PublishedDocumentVersionId(idGenerator.NewId(now)), title, body, now);
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

        return PublishedDocumentErrors.PublishConflict(documentKey);
    }

    private static PublishedDocumentVersionDto ToDto(PublishedDocumentVersion version) =>
        new(version.DocumentKey, version.Version, version.Sequence, version.Title, version.Body, version.PublishedAt);
}
