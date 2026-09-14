using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;

/// <summary>
/// `file-storage.md`'s Access control section: a presigned GET only after the caller is proven a
/// participant of the attachment's conversation, cached per (attachment, viewer) for slightly less
/// than the URL's own lifetime - the same cache-aside shape `CheckCorsOriginHandler` established,
/// with a reference-type wrapper record for the same `ICache`-constraint reason (see that handler's
/// own remarks).
///
/// <para><b>`23-82`/`23-80`: also the one place that counts a download</b> - "the presigned GET is
/// issued" (`23-82`'s own words for where to count) is exactly the <c>cache.GetOrCreateAsync</c>
/// factory below, which runs only on a cache miss. Two writes happen there: the attachment's own
/// <see cref="Domain.Attachment.RecordDownload"/> (23-80's "ever been downloaded" filter) and
/// <see cref="IAttachmentEgressMeter.RecordAsync"/> (23-82's maintained per-tenant-per-month
/// aggregate) - both best-effort, wrapped so a measurement failure never turns a successful download
/// into a failed request (`23-82`'s own "refusing a download is worse than refusing an upload," read
/// the rest of the way: a download that *worked* must not become an error because the count of it
/// did not).</para>
///
/// <para><b>`25-83`: also the one place the hard download-block threshold is enforced</b> -
/// <see cref="EnforceDownloadCapAsync"/>, called before the cache is ever consulted, on both the
/// operator and the visitor path alike. Deliberately no carve-out for either caller
/// (`docs/backlog/25-83-*.md`'s own decision, read against `23-82`'s own Scope "refusing a download is
/// worse than refusing an upload": a tenant is responsible for its own visitors' experience the same
/// way it is responsible for everything else about its account). The check reads
/// <see cref="Site.DownloadBlockExempt"/> and the current month's egress live off a freshly loaded
/// <see cref="Site"/> and <see cref="IAttachmentEgressReadStore"/> on every call - never cached,
/// because both are exactly the kind of compare-and-set-adjacent read CLAUDE.md rule 8 forbids caching
/// when a write decision (minting a presigned URL) depends on it.</para>
/// </summary>
public sealed class GetAttachmentDownloadUrlHandler(
    IAttachmentRepository attachments,
    IConversationRepository conversations,
    ISiteRepository sites,
    IFileStorage fileStorage,
    IPermissionChecker permissions,
    ICache cache,
    IAttachmentEgressMeter egressMeter,
    IAttachmentEgressReadStore egressReads,
    IDownloadThresholdReadStore thresholds,
    AttachmentOptions options,
    IClock clock,
    IIdGenerator idGenerator,
    ILogger<GetAttachmentDownloadUrlHandler> logger)
{
    public Task<Result<AttachmentDownload>> HandleAsVisitorAsync(
        GetAttachmentDownloadUrlAsVisitor query, CancellationToken cancellationToken) =>
        HandleAsync(
            query.AttachmentId,
            query.RequestedBy.Value,
            isVisitor: true,
            conversation => conversation.VisitorId == query.RequestedBy
                ? null
                : ConversationErrors.Forbidden("This visitor is not a participant of this conversation."),
            cancellationToken);

    public async Task<Result<AttachmentDownload>> HandleAsOperatorAsync(
        GetAttachmentDownloadUrlAsOperator query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        return await HandleAsync(
            query.AttachmentId,
            query.RequestedBy.Value,
            isVisitor: false,
            conversation => conversation.OperatorId == query.RequestedBy
                ? null
                : ConversationErrors.Forbidden("This operator is not assigned to this conversation."),
            cancellationToken);
    }

    private async Task<Result<AttachmentDownload>> HandleAsync(
        AttachmentId attachmentId,
        Guid viewerId,
        bool isVisitor,
        Func<Conversation, Error?> checkParticipant,
        CancellationToken cancellationToken)
    {
        var attachment = await attachments.GetByIdAsync(attachmentId, cancellationToken);
        if (attachment is null)
        {
            return ConversationErrors.AttachmentNotFound(attachmentId.Value);
        }

        var conversation = await conversations.GetByIdAsync(attachment.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.AttachmentNotFound(attachmentId.Value);
        }

        if (checkParticipant(conversation) is { } forbidden)
        {
            return forbidden;
        }

        if (await EnforceDownloadCapAsync(attachment, conversation, isVisitor, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        if (attachment.State == AttachmentState.Deleted)
        {
            // `23-80`: a distinct, permanent code - see ConversationErrors.AttachmentRemoved's own
            // remarks for why this is not folded into AttachmentNotReady below.
            return ConversationErrors.AttachmentRemoved(attachmentId.Value);
        }

        if (attachment.State != AttachmentState.Ready)
        {
            return ConversationErrors.AttachmentNotReady($"Attachment {attachmentId.Value} is not ready for download.");
        }

        var key = AttachmentCacheKeys.ForDownload(attachmentId, viewerId);
        // "Slightly less than lifetime" (file-storage.md): a cache entry that outlived the URL it
        // holds would hand out a dead link for the remainder of its TTL.
        var cacheTtl = TimeSpan.FromTicks(options.DownloadLifetime.Ticks * 4 / 5);
        var cached = await cache.GetOrCreateAsync(
            key,
            async ct =>
            {
                var url = await fileStorage.CreateDownloadUrlAsync(
                    new ObjectKey(attachment.ObjectKey), options.DownloadLifetime, ct);
                // `5-10`: the same cached entry carries the thumbnail's own presigned URL, one
                // presign call each - not two round trips through this handler for a client that
                // wants both. Only presigned when `5-04`'s job actually produced one; a non-image
                // attachment's `ThumbnailKey` stays null forever, and this stays null right with it.
                var thumbnailUrl = attachment.ThumbnailKey is { } thumbnailKey
                    ? await fileStorage.CreateDownloadUrlAsync(new ObjectKey(thumbnailKey), options.DownloadLifetime, ct)
                    : null;

                await RecordDownloadAsync(attachment, ct);

                return new CachedDownload(url, attachment.ContentType, thumbnailUrl, clock.UtcNow.Add(options.DownloadLifetime));
            },
            new CacheEntryOptions(cacheTtl),
            cancellationToken);

        return new AttachmentDownload(cached.Url, cached.ContentType, cached.ThumbnailUrl, cached.ExpiresAt);
    }

    /// <summary>`23-82`/`23-80`'s own two counters, written only when the block above actually mints
    /// a fresh presigned URL. Deliberately swallows its own failure (logged, not rethrown) - a caller
    /// who successfully reached this point already has a working download URL, and losing the count of
    /// one download is a strictly smaller problem than turning a working download into a server
    /// error.</summary>
    private async Task RecordDownloadAsync(Attachment attachment, CancellationToken cancellationToken)
    {
        try
        {
            var now = clock.UtcNow;
            attachment.RecordDownload(now);
            await attachments.SaveAsync(attachment, cancellationToken);

            var periodMonth = new DateOnly(now.Year, now.Month, 1);
            await egressMeter.RecordAsync(attachment.SiteId, periodMonth, attachment.SizeBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not record the download of attachment {AttachmentId}; the presigned URL was still issued.",
                attachment.Id.Value);
        }
    }

    /// <summary>`25-83`: the hard-threshold gate itself. Returns the refusal to hand back to the
    /// caller, or <see langword="null"/> when the download may proceed - a site that does not exist
    /// (defensive only: <see cref="Attachment.SiteId"/> always names a real row, the same foreign-key
    /// guarantee <see cref="Infrastructure.Postgres.SiteAttachmentStorageBudgetStore"/>'s own remarks
    /// note for an identical "unreachable in production" case) is treated as not exempt and measured
    /// at zero, which resolves to "not blocked" rather than throwing - the one place this handler
    /// would rather fail open than 500 a caller who did nothing wrong.
    ///
    /// <para><b>Read order: the exemption flag first, the egress figure only if it is not set.</b>
    /// This is this item's own answer to "the owner's override toggle and the hard block crossing at
    /// the same moment need a defined ordering" (`docs/backlog/25-83-*.md`'s own "Where this is likely
    /// to go wrong"): both facts are read live, with no caching, but they are not read inside one
    /// shared transaction (<see cref="Site"/> comes from <c>AgoChatDbContext</c>, the egress figure
    /// from a separate Dapper connection - the same split <see cref="IAttachmentEgressReadStore"/>'s
    /// own remarks already accept for the write side). A request that reads <c>Exempt = false</c> a
    /// moment before the owner grants the exemption is refused once, incorrectly, and succeeds on
    /// retry - undercounting the exemption exactly the same "safe direction" this handler's own class
    /// remarks already accept for <see cref="IAttachmentEgressMeter"/>'s undercounted egress: a false
    /// refusal (denying a request that should have passed) costs one retry, while the reverse (a
    /// window in which a request that should be blocked slips through) would be an actual bypass of an
    /// owner-controlled security decision. Building a single cross-store snapshot to close this window
    /// entirely would need either moving the exemption flag into the Dapper-only egress store or the
    /// egress figure into `AgoChatDbContext`, both larger changes than this narrow race justifies.</para>
    /// </summary>
    private async Task<Error?> EnforceDownloadCapAsync(
        Attachment attachment, Conversation conversation, bool isVisitor, CancellationToken cancellationToken)
    {
        var site = await sites.GetByIdAsync(attachment.SiteId, cancellationToken);
        if (site is null || site.DownloadBlockExempt)
        {
            return null;
        }

        var now = clock.UtcNow;
        var periodMonth = new DateOnly(now.Year, now.Month, 1);
        var egress = await egressReads.GetForSiteAsync(attachment.SiteId, periodMonth, cancellationToken);
        var tierThresholds = await thresholds.GetForTierAsync(site.Tier, cancellationToken);

        if (egress.BytesOut < tierThresholds.HardThresholdBytes)
        {
            return null;
        }

        if (isVisitor)
        {
            await TryAddDownloadBlockedMessageAsync(conversation, site.Locale.ToString(), now, cancellationToken);
        }

        return ConversationErrors.AttachmentDownloadBlocked(attachment.SiteId.Value);
    }

    /// <summary>`25-83`: "a system message lands directly in the conversation where a visitor's
    /// download was refused" - the exact locale-resolution and text shape
    /// `RouteConversationToModuleHandler`'s own four texts (`25-64`/`25-66`) already establish
    /// (`Locale.Ru` gets natural Russian wording, every other locale keeps English), reused here rather
    /// than re-derived.
    ///
    /// <para><b>Best-effort, deliberately not retried against a reloaded <see cref="Conversation"/> on
    /// a concurrency conflict</b> - unlike <c>RouteConversationToModuleHandler.AddSystemMessageAndSaveAsync</c>'s
    /// own full reload-and-retry loop. That machinery exists there to make a module-task state
    /// transition durable under contention; this is a courtesy notice riding on a conversation this
    /// handler only read, never a transition it owns, and losing one notice under a genuine race (an
    /// operator or another download racing the very same conversation row) is a strictly smaller
    /// problem than adding this handler's first multi-attempt write loop to make a best-effort message
    /// land - the identical "swallow, log, keep the caller's own real result" posture
    /// <see cref="RecordDownloadAsync"/> already takes for its own two counters, restated here for a
    /// third best-effort write on the same request.</para>
    /// </summary>
    private async Task TryAddDownloadBlockedMessageAsync(
        Conversation conversation, string locale, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            // `RouteConversationToModuleHandler`'s own precedent (see this method's class-level
            // remarks) - `IIdGenerator.NewId(now)`, never `Guid.NewGuid()` directly: CLAUDE.md rule 2
            // bans a raw `Guid.NewGuid()` inside Application, and ids here are UUID v7, time-ordered
            // off the same `now` this message is stamped with, not random.
            var messageId = new MessageId(idGenerator.NewId(now));
            conversation.AddSystemMessage(messageId, new MessageBody(DownloadBlockedText(locale)), now);
            await conversations.SaveAsync(conversation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // InvalidConversationStateException (the conversation closed between load and here) and
            // ConversationConcurrencyConflictException both land here - this method's own remarks on
            // why neither is worth a retry loop.
            logger.LogWarning(
                ex,
                "Could not add the download-blocked notice to conversation {ConversationId}; the download was still refused.",
                conversation.Id.Value);
        }
    }

    /// <summary>See <see cref="RouteConversationToModule.RouteConversationToModuleHandler"/>'s own
    /// four sibling texts for the shape this one matches exactly.</summary>
    private static string DownloadBlockedText(string locale) => locale == nameof(Locale.Ru)
        ? "Клиент попытался скачать файл, но аккаунт достиг месячного лимита скачиваний — загрузка заблокирована."
        : "A customer tried to download a file, but this account has reached its monthly download limit - the download was blocked.";

    private sealed record CachedDownload(Uri Url, string ContentType, Uri? ThumbnailUrl, DateTimeOffset ExpiresAt);
}
