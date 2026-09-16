using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.GrantAttachmentUpload;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RevokeAttachmentUpload;

/// <summary>
/// `23-78`: the reverse of <see cref="Application.UseCases.GrantAttachmentUpload.GrantAttachmentUploadHandler"/> -
/// same permission, same per-conversation assignment comparison, same one-statement atomic write,
/// restated for the opposite direction. Reuses <see cref="AttachmentUploadGrantStatus"/> from that
/// handler's own file rather than a second, near-identical response type - both actions answer the
/// identical question ("what is this conversation's attachment-upload grant state now").
///
/// <para>Revoking a conversation that was granted only by the tenant-level default
/// (<see cref="Domain.WidgetConfig.AllowAttachmentUploadsByDefault"/>, seeded at
/// <see cref="Conversation.Start"/> with no operator behind it) is not a special case here - the
/// repository's own <c>UPDATE ... WHERE attachment_upload_granted_at IS NOT NULL</c> does not care
/// whether <c>attachment_upload_granted_by</c> was ever populated, and clearing both columns is exactly
/// as correct a reversal either way (`IConversationAttachmentUploadGrantRepository`'s own remarks).</para>
///
/// <para>`25-110`: the outbox enqueue + <see cref="IUnitOfWork"/> transaction + "flush via
/// <see cref="IConversationRepository.SaveAsync"/> on an untouched, still-<c>Unchanged</c> aggregate"
/// shape is identical to <see cref="Application.UseCases.GrantAttachmentUpload.GrantAttachmentUploadHandler"/>'s
/// own - see that type's own remarks for the full reasoning, restated here only for
/// <see cref="AttachmentUploadGrantChangedMapper"/>'s <c>granted: false</c> direction.</para>
/// </summary>
public sealed class RevokeAttachmentUploadHandler(
    IConversationRepository conversations,
    IConversationAttachmentUploadGrantRepository grants,
    IPermissionChecker permissions,
    IUnitOfWork unitOfWork,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<AttachmentUploadGrantStatus>> HandleAsync(
        RevokeAttachmentUpload command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationAttachmentUploadGrant, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to revoke attachment uploads for this site.");
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

        var now = clock.UtcNow;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var outcome = await grants.RevokeAsync(command.ConversationId, command.SiteId, command.RequestedBy, now, cancellationToken);

        if (outcome == AttachmentUploadGrantOutcome.Applied)
        {
            // `25-110`: enqueued only on an actual transition - a visitor mid-upload when this fires
            // must lose the icon live too (the item's own "revoke matters as much as grant").
            outbox.Enqueue(AttachmentUploadGrantChangedMapper.ToEnvelope(
                command.ConversationId, conversation.VisitorId, granted: false, now, idGenerator));

            // Flushes the outbox row staged above, and only that - GrantAttachmentUploadHandler's own
            // remarks explain why this cannot reintroduce the xmin race this handler was built to avoid.
            await conversations.SaveAsync(conversation, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return outcome switch
        {
            AttachmentUploadGrantOutcome.NotFound => ConversationErrors.NotFound(command.ConversationId.Value),
            AttachmentUploadGrantOutcome.AlreadyInState =>
                ConversationErrors.ConversationAttachmentUploadNotGranted(command.ConversationId.Value),
            AttachmentUploadGrantOutcome.Applied => new AttachmentUploadGrantStatus(command.ConversationId, now, command.RequestedBy),
            _ => throw new InvalidOperationException($"Unhandled {nameof(AttachmentUploadGrantOutcome)}: {outcome}."),
        };
    }
}
