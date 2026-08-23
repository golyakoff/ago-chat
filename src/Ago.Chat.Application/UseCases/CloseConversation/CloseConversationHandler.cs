using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.CloseConversation;

/// <summary>
/// `6-02`: the first real caller of `Conversation.Close()` since Stage 1 - the domain method has
/// existed with no use case, no endpoint, and no integration event wired to it. Operator-only (a
/// visitor ending a chat session client-side is not the same as closing the record - the backlog
/// item's own scope note); only the operator already assigned to *this* conversation may close it,
/// checked here rather than inside <see cref="Conversation.Close"/> itself - unlike
/// <c>AddOperatorMessage</c>, <c>Close</c> takes no <see cref="OperatorId"/> parameter to check
/// against, so the "is this caller the one assigned to this conversation" fact
/// (adr/0016: a fact about the conversation, not a permission) is this handler's own job, the same
/// "RBAC answers may this operator act at all, a per-conversation comparison answers on this one"
/// split <see cref="Application.UseCases.ConfirmAttachment.ConfirmAttachmentHandler.HandleAsOperatorAsync"/>
/// already draws for `conversation:send`. A conversation that was never assigned (still `Waiting`) is
/// therefore not closable by anyone yet either - closing is scoped to "the operator handling this
/// conversation ends it," not a moderator force-close of an unclaimed one; that would be new scope.
///
/// Injects <see cref="IOutboxWriter"/> directly rather than staging through
/// <c>Infrastructure.Postgres.Pipeline</c> - the same "plain, unbatched per-request handler, no shared
/// multi-conversation transaction to coordinate" shape <see cref="Application.UseCases.ConfirmAttachment.ConfirmAttachmentHandler"/>
/// uses (adr/0005: state change and integration event, one transaction, one `SaveChangesAsync`).
/// </summary>
public sealed class CloseConversationHandler(
    IConversationRepository conversations,
    IPermissionChecker permissions,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result> HandleAsync(CloseConversation command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.SiteId, Permission.ConversationClose, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to close conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.OperatorId != command.OperatorId)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        try
        {
            conversation.Close(clock.UtcNow);
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }

        var domainEvent = conversation.DomainEvents.OfType<ConversationClosed>().Single();
        outbox.Enqueue(ConversationClosedMapper.ToEnvelope(domainEvent, idGenerator));
        conversation.ClearDomainEvents();

        await conversations.SaveAsync(conversation, cancellationToken);
        return Result.Success();
    }
}
