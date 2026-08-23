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

        try
        {
            return await CloseAndSaveAsync(conversation, command, cancellationToken);
        }
        catch (ConversationConcurrencyConflictException)
        {
            // `6-08`: a concurrent writer (typically a message send bumping this row's `xmin`, `6-06`'s
            // load-proof finding) committed between the read above and the save inside
            // CloseAndSaveAsync - not that closing itself is wrong. Reloading and reapplying is safe
            // exactly because Close() re-validates its own invariant against whatever is actually on
            // disk now: if a second racing close (or any other state change) makes the fresh row
            // unclosable, that surfaces as the ordinary Conversation.InvalidState/Forbidden result
            // below, not a swallowed exception - the retry never bypasses a real business conflict, it
            // only re-asks the same question against fresh data. Retried once, not in a loop: a second
            // ConversationConcurrencyConflictException in the same request means a third writer landed
            // inside this already-narrow window, and at that point the honest answer is "retry the
            // whole request" (Conversation.ConcurrencyConflict, 409), matching this item's "single
            // transparent retry, or a clean 409" scope - never an unbounded retry loop.
            var fresh = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
            if (fresh is null)
            {
                return ConversationErrors.NotFound(command.ConversationId.Value);
            }

            try
            {
                return await CloseAndSaveAsync(fresh, command, cancellationToken);
            }
            catch (ConversationConcurrencyConflictException)
            {
                return ConversationErrors.ConcurrencyConflict(command.ConversationId.Value);
            }
        }
    }

    private async Task<Result> CloseAndSaveAsync(
        Conversation conversation, CloseConversation command, CancellationToken cancellationToken)
    {
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

        // May throw ConversationConcurrencyConflictException (IConversationRepository's own contract,
        // `6-08`) - left to propagate to HandleAsync's retry wrapper rather than caught here, so this
        // method stays "the one attempt" and HandleAsync stays the one place that owns the
        // retry-once policy.
        await conversations.SaveAsync(conversation, cancellationToken);
        return Result.Success();
    }
}
