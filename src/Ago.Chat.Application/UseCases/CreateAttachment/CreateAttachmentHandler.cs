using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.CreateAttachment;

/// <summary>
/// One handler, two entry points - the same shape as <c>GetConversationHistoryHandler</c>: a visitor
/// and an operator differ only in *how access is checked* (participant-of-conversation vs.
/// RBAC-permission-plus-assigned-operator), everything after that - rate limit, content-type/size
/// validation, presign, persist - is identical.
///
/// References <c>Ago.Platform.Abstractions.IFileStorage</c> directly, per `clean-architecture.md`:
/// generic technical ports live in the platform's dependency-free abstractions package and are safe
/// for Application to reference inwards; only the *implementation* (`Ago.Platform.Storage.S3`) is an
/// Infrastructure concern this layer never sees.
/// </summary>
public sealed class CreateAttachmentHandler(
    IConversationRepository conversations,
    IAttachmentRepository attachments,
    IFileStorage fileStorage,
    IRateLimiter rateLimiter,
    IPermissionChecker permissions,
    AttachmentOptions options,
    AttachmentRateLimitOptions rateLimitOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<PresignedAttachmentUpload>> HandleAsVisitorAsync(
        CreateAttachmentAsVisitor command, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.VisitorId != command.RequestedBy)
        {
            return ConversationErrors.Forbidden("This visitor is not a participant of this conversation.");
        }

        var visitorLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"attachment-create:visitor:{command.RequestedBy.Value}"),
            new RateLimitRule(rateLimitOptions.PerVisitorCapacity, rateLimitOptions.PerVisitorRefillPerSecond),
            cancellationToken);
        if (!visitorLimit.Allowed)
        {
            return ConversationErrors.RateLimited(visitorLimit.RetryAfter);
        }

        return await CreateAsync(conversation, command.ContentType, command.DeclaredSizeBytes, cancellationToken);
    }

    public async Task<Result<PresignedAttachmentUpload>> HandleAsOperatorAsync(
        CreateAttachmentAsOperator command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages for this site.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.OperatorId != command.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var operatorLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"attachment-create:operator:{command.RequestedBy.Value}"),
            new RateLimitRule(rateLimitOptions.PerOperatorCapacity, rateLimitOptions.PerOperatorRefillPerSecond),
            cancellationToken);
        if (!operatorLimit.Allowed)
        {
            return ConversationErrors.RateLimited(operatorLimit.RetryAfter);
        }

        return await CreateAsync(conversation, command.ContentType, command.DeclaredSizeBytes, cancellationToken);
    }

    private async Task<Result<PresignedAttachmentUpload>> CreateAsync(
        Conversation conversation, string contentType, long declaredSizeBytes, CancellationToken cancellationToken)
    {
        // Per-site last, after the caller's own bucket - a caller who was never going to pass their
        // own limit should not also spend a share of the site's budget finding that out
        // (`SendVisitorMessageHandler`'s own ordering, mirrored here).
        var siteLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"attachment-create:site:{conversation.SiteId.Value}"),
            new RateLimitRule(rateLimitOptions.PerSiteCapacity, rateLimitOptions.PerSiteRefillPerSecond),
            cancellationToken);
        if (!siteLimit.Allowed)
        {
            return ConversationErrors.RateLimited(siteLimit.RetryAfter);
        }

        if (!options.AllowedContentTypes.TryGetValue(contentType, out var extension))
        {
            return ConversationErrors.AttachmentInvalidContentType(contentType);
        }

        if (declaredSizeBytes <= 0 || declaredSizeBytes > options.MaxSizeBytes)
        {
            return ConversationErrors.AttachmentTooLarge(declaredSizeBytes, options.MaxSizeBytes);
        }

        var now = clock.UtcNow;
        var attachmentId = new AttachmentId(idGenerator.NewId(now));
        var objectKey = $"site/{conversation.SiteId.Value}/conv/{conversation.Id.Value}/{attachmentId.Value}{extension}";

        var presigned = await fileStorage.CreateUploadAsync(
            new ObjectKey(objectKey),
            new UploadConstraints(contentType, declaredSizeBytes, options.UploadLifetime),
            cancellationToken);

        var attachment = Attachment.CreatePending(
            attachmentId, conversation.SiteId, conversation.Id, objectKey, contentType, declaredSizeBytes, now);
        await attachments.SaveAsync(attachment, cancellationToken);

        return new PresignedAttachmentUpload(attachmentId.Value, presigned.Url, presigned.ExpiresAt);
    }
}
