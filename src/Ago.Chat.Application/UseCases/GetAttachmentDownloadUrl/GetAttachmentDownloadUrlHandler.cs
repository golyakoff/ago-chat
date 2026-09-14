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
/// </summary>
public sealed class GetAttachmentDownloadUrlHandler(
    IAttachmentRepository attachments,
    IConversationRepository conversations,
    IFileStorage fileStorage,
    IPermissionChecker permissions,
    ICache cache,
    IAttachmentEgressMeter egressMeter,
    AttachmentOptions options,
    IClock clock,
    ILogger<GetAttachmentDownloadUrlHandler> logger)
{
    public Task<Result<AttachmentDownload>> HandleAsVisitorAsync(
        GetAttachmentDownloadUrlAsVisitor query, CancellationToken cancellationToken) =>
        HandleAsync(
            query.AttachmentId,
            query.RequestedBy.Value,
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
            conversation => conversation.OperatorId == query.RequestedBy
                ? null
                : ConversationErrors.Forbidden("This operator is not assigned to this conversation."),
            cancellationToken);
    }

    private async Task<Result<AttachmentDownload>> HandleAsync(
        AttachmentId attachmentId,
        Guid viewerId,
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

    private sealed record CachedDownload(Uri Url, string ContentType, Uri? ThumbnailUrl, DateTimeOffset ExpiresAt);
}
