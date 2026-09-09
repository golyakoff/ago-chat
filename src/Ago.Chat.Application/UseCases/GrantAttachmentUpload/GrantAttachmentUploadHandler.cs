using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GrantAttachmentUpload;

/// <summary>
/// `23-78`: the operator's own side of the control - "an operator ticks «разрешаю пользователю
/// отправлять файлы», and without it there is no upload control at all" (the backlog item's own
/// Decision). Gated by <see cref="Permission.ConversationAttachmentUploadGrant"/> - see that field's
/// own remarks for why this is Operator-scoped, not Admin-scoped like
/// <see cref="Application.UseCases.BlockConversation.BlockConversationHandler"/>.
///
/// <para><b>Loads the aggregate for the assignment check, writes through the raw-SQL repository.</b>
/// Unlike <see cref="Application.UseCases.BlockConversation.BlockConversationHandler"/> (which never
/// loads <see cref="Conversation"/> at all - blocking is permission-only, any operator holding
/// <see cref="Permission.ConversationBlock"/> may act on any conversation on the site), granting an
/// upload is scoped to "the operator handling this conversation" - the identical "RBAC answers may this
/// operator act at all, a per-conversation comparison answers on this one" split
/// <c>CloseConversationHandler</c>/<c>CreateAttachmentHandler.HandleAsOperatorAsync</c> already draw for
/// <see cref="Permission.ConversationSend"/>. That comparison needs <see cref="Conversation.OperatorId"/>,
/// which only a real load can answer - so this handler pays for one <see cref="IConversationRepository.GetByIdAsync"/>
/// read, but never calls <see cref="IConversationRepository.SaveAsync"/>: the actual write is
/// <see cref="IConversationAttachmentUploadGrantRepository.GrantAsync"/>, the same raw-SQL bypass
/// <see cref="Application.UseCases.BlockConversation.BlockConversationHandler"/> uses for the identical
/// xmin-racing reason (<see cref="IConversationAttachmentUploadGrantRepository"/>'s own remarks) - this
/// handler's own read never risks a concurrency conflict because it never saves the row it loaded.</para>
/// </summary>
public sealed class GrantAttachmentUploadHandler(
    IConversationRepository conversations,
    IConversationAttachmentUploadGrantRepository grants,
    IPermissionChecker permissions,
    IClock clock)
{
    public async Task<Result<AttachmentUploadGrantStatus>> HandleAsync(
        GrantAttachmentUpload command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationAttachmentUploadGrant, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to grant attachment uploads for this site.");
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
        var outcome = await grants.GrantAsync(command.ConversationId, command.SiteId, command.RequestedBy, now, cancellationToken);

        return outcome switch
        {
            AttachmentUploadGrantOutcome.NotFound => ConversationErrors.NotFound(command.ConversationId.Value),
            AttachmentUploadGrantOutcome.AlreadyInState =>
                ConversationErrors.ConversationAttachmentUploadAlreadyGranted(command.ConversationId.Value),
            AttachmentUploadGrantOutcome.Applied => new AttachmentUploadGrantStatus(command.ConversationId, now, command.RequestedBy),
            _ => throw new InvalidOperationException($"Unhandled {nameof(AttachmentUploadGrantOutcome)}: {outcome}."),
        };
    }
}

/// <summary>The grant/revoke actions' own success response - both
/// <see cref="GrantAttachmentUploadHandler"/> and
/// <see cref="Application.UseCases.RevokeAttachmentUpload.RevokeAttachmentUploadHandler"/> return this,
/// since both answer the identical question ("what is this conversation's attachment-upload grant
/// state now") for opposite directions - the same reuse
/// <c>BlockConversationHandler</c>/<c>UnblockConversationHandler</c>'s own shared
/// <c>ConversationBlockStatus</c> already establishes.</summary>
public sealed record AttachmentUploadGrantStatus(ConversationId ConversationId, DateTimeOffset OccurredAt, OperatorId OperatorId);
