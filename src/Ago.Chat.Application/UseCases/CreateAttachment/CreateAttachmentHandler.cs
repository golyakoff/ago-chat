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
/// `23-78`: the one exception is <see cref="Domain.Conversation.HasAttachmentUploadGrant"/>, checked
/// only in <see cref="HandleAsVisitorAsync"/> - "an operator's own uploads are unaffected" is this
/// item's own Scope, and correctly so: an operator is an authenticated, paid identity already gated by
/// <see cref="Permission.ConversationSend"/> and the conversation's own assignment, not the anonymous
/// party this grant exists to slow down.
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
    IConversationAttachmentBudget conversationBudget,
    IUnitOfWork unitOfWork,
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

        // `23-78`: the control itself. Checked before the rate limiter right below, on the same
        // "a caller who was never going to pass should not also spend a shared budget finding that
        // out" ordering this method's own site-limit comment states below - an anonymous flood against
        // an ungranted conversation must not cost this visitor's own rate-limit bucket anything, since
        // that bucket exists to bound a *legitimate* visitor's own burst, not to be the thing that
        // eventually stops an attacker who was refused before it was ever consulted. Hiding the
        // widget's own upload icon is a consequence of this state, never the control - see
        // `docs/backlog/23-78-*.md`'s own "The distinction that decides whether this works".
        if (!conversation.HasAttachmentUploadGrant)
        {
            return ConversationErrors.AttachmentUploadNotGranted(conversation.Id.Value);
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

        // `23-75`: the conversation's own byte budget, reserved atomically inside the same
        // transaction that is about to create this attachment's `pending` row - CLAUDE.md rule 8, and
        // IConversationAttachmentBudget's own remarks on why a cached or separately-read total cannot
        // be trusted here. Presigning happens *inside* this transaction too (immediately below), not
        // before it: `GetPreSignedUrlRequest` is a local signing computation, not a network call to
        // storage, so the row lock this reservation's UPDATE holds is held for microseconds, not for
        // an S3 round trip - and folding it in here means a presign failure after a successful
        // reservation rolls the reservation back with it, rather than leaking a reservation with no
        // attachment row for the sweep to ever find.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var reservation = await conversationBudget.TryReserveAsync(
            conversation.Id, declaredSizeBytes, options.MaxConversationBytes, cancellationToken);
        if (!reservation.Reserved)
        {
            // Disposing `transaction` without a commit rolls back - even the no-op write
            // ConversationAttachmentBudgetStore's own locked read-then-write issues on a refusal
            // (see its own remarks) never survives, so the conversation's row is left exactly as it
            // was. The same shape every other mid-transaction refusal in this codebase uses
            // (TransferConversationHandler's own remarks).
            return ConversationErrors.AttachmentConversationBudgetExceeded(declaredSizeBytes, reservation.RemainingBytes);
        }

        var presigned = await fileStorage.CreateUploadAsync(
            new ObjectKey(objectKey),
            new UploadConstraints(contentType, declaredSizeBytes, options.UploadLifetime),
            cancellationToken);

        var attachment = Attachment.CreatePending(
            attachmentId, conversation.SiteId, conversation.Id, objectKey, contentType, declaredSizeBytes, now);
        await attachments.SaveAsync(attachment, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new PresignedAttachmentUpload(attachmentId.Value, presigned.Url, presigned.ExpiresAt);
    }
}
