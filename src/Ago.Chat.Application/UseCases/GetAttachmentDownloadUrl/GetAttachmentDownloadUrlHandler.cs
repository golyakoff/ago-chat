using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;

/// <summary>
/// `file-storage.md`'s Access control section: a presigned GET only after the caller is proven a
/// participant of the attachment's conversation, cached per (attachment, viewer) for slightly less
/// than the URL's own lifetime - the same cache-aside shape `CheckCorsOriginHandler` established,
/// with a reference-type wrapper record for the same `ICache`-constraint reason (see that handler's
/// own remarks).
/// </summary>
public sealed class GetAttachmentDownloadUrlHandler(
    IAttachmentRepository attachments,
    IConversationRepository conversations,
    IFileStorage fileStorage,
    IPermissionChecker permissions,
    ICache cache,
    AttachmentOptions options,
    IClock clock)
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
                return new CachedDownload(url, clock.UtcNow.Add(options.DownloadLifetime));
            },
            new CacheEntryOptions(cacheTtl),
            cancellationToken);

        return new AttachmentDownload(cached.Url, cached.ExpiresAt);
    }

    private sealed record CachedDownload(Uri Url, DateTimeOffset ExpiresAt);
}
