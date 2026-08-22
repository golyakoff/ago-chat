using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ConfirmAttachment;

/// <summary>
/// `5-03`'s Done-when: "confirming without a real upload / mismatched size or type fails, stays
/// pending" - the client's own "I uploaded it" claim is never trusted (`file-storage.md`). This
/// handler is the one place that calls <see cref="IFileStorage.GetMetadataAsync"/> to find out what
/// is actually on the object store before flipping <see cref="Domain.Attachment"/> to
/// <see cref="AttachmentState.Ready"/>.
/// </summary>
public sealed class ConfirmAttachmentHandler(
    IAttachmentRepository attachments,
    IConversationRepository conversations,
    IFileStorage fileStorage,
    IPermissionChecker permissions,
    IClock clock)
{
    public async Task<Result> HandleAsVisitorAsync(ConfirmAttachmentAsVisitor command, CancellationToken cancellationToken)
    {
        var attachment = await attachments.GetByIdAsync(command.AttachmentId, cancellationToken);
        if (attachment is null)
        {
            return ConversationErrors.AttachmentNotFound(command.AttachmentId.Value);
        }

        var conversation = await conversations.GetByIdAsync(attachment.ConversationId, cancellationToken);
        if (conversation is null || conversation.VisitorId != command.RequestedBy)
        {
            return ConversationErrors.Forbidden("This visitor is not a participant of this conversation.");
        }

        return await ConfirmAsync(attachment, cancellationToken);
    }

    public async Task<Result> HandleAsOperatorAsync(ConfirmAttachmentAsOperator command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages for this site.");
        }

        var attachment = await attachments.GetByIdAsync(command.AttachmentId, cancellationToken);
        if (attachment is null)
        {
            return ConversationErrors.AttachmentNotFound(command.AttachmentId.Value);
        }

        var conversation = await conversations.GetByIdAsync(attachment.ConversationId, cancellationToken);
        if (conversation is null || conversation.OperatorId != command.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        return await ConfirmAsync(attachment, cancellationToken);
    }

    private async Task<Result> ConfirmAsync(Attachment attachment, CancellationToken cancellationToken)
    {
        var metadata = await fileStorage.GetMetadataAsync(new ObjectKey(attachment.ObjectKey), cancellationToken);
        if (metadata is null)
        {
            return ConversationErrors.AttachmentVerificationFailed("No upload found for this attachment yet.");
        }

        try
        {
            attachment.ConfirmReady(metadata.SizeBytes, metadata.ContentType, clock.UtcNow);
        }
        catch (AttachmentVerificationMismatchException ex)
        {
            return ConversationErrors.AttachmentVerificationFailed(ex.Message);
        }
        catch (InvalidAttachmentStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }

        await attachments.SaveAsync(attachment, cancellationToken);
        return Result.Success();
    }
}
