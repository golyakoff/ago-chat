using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AcknowledgeMessageDelivered;

/// <summary>
/// `25-119`: the widget's own delivery ack, the write half of the item. Loads the full
/// <see cref="Conversation"/> aggregate via <see cref="IConversationRepository.GetByIdAsync"/> - the
/// same visitor-authorization shape every other visitor-facing entry point in this codebase already
/// pays for (<see cref="Application.UseCases.GetConversationHistory.GetConversationHistoryHandler.HandleAsVisitorAsync"/>'s
/// own identical <c>conversation.VisitorId != query.RequestedBy</c> check) - and then persists the
/// mutation through <see cref="IConversationRepository.SaveAsync"/> rather than a
/// <c>IConversationAttachmentUploadGrantRepository</c>-shaped raw-SQL bypass.
///
/// <para><b>Why the aggregate's own <c>SaveAsync</c> is safe here, unlike a grant/revoke.</b>
/// <see cref="Application.Abstractions.IConversationAttachmentUploadGrantRepository"/>'s own remarks
/// explain why <em>that</em> write bypasses the aggregate: flipping a column on <c>conversations</c>
/// through a load-mutate-save stakes that row's own <c>xmin</c> against every ordinary message send on
/// the same conversation, exactly the collision `25-109` found live (<c>RecordUnreadMessageHandler</c>'s
/// own remarks) between <c>UnreadCounterConsumer</c> and <c>MessageBatchWriter</c>'s per-flush
/// <c>SaveChangesAsync</c>. This write never touches that row at all: <see cref="Message.MarkDelivered"/>
/// mutates one row of the tracked <c>_messages</c> collection, and <c>messages</c> rows are append-only
/// everywhere else in this codebase (nothing else ever issues a second write against an existing one) -
/// so there is no concurrent writer of this exact row to race, and <c>SaveChangesAsync</c> emits a plain
/// <c>UPDATE ... WHERE id = @id</c> with no <c>xmin</c> comparison at all (<c>MessageConfiguration</c>
/// declares no concurrency token on <see cref="Message"/>). The parent <see cref="Conversation"/> stays
/// <c>Unchanged</c> in the tracker (nothing here calls a method on it), so its own row is never even sent
/// an <c>UPDATE</c> - the identical "only the thing that actually changed gets written" shape
/// <c>GrantAttachmentUploadHandler</c>'s own remarks describe for its own final <c>SaveAsync</c> call.</para>
/// </summary>
public sealed class AcknowledgeMessageDeliveredHandler(
    IConversationRepository conversations,
    IUnitOfWork unitOfWork,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result> HandleAsync(AcknowledgeMessageDelivered command, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.VisitorId != command.VisitorId)
        {
            return ConversationErrors.Forbidden("This visitor is not a participant of this conversation.");
        }

        // `25-119`: one code for "no such message" and "a real message, but not one an operator wrote" -
        // ConversationErrors.MessageNotFound's own remarks state why a visitor's own client has no
        // legitimate way to construct either case, so there is nothing more specific worth telling it.
        var message = conversation.Messages.FirstOrDefault(m => m.Id == command.MessageId);
        if (message is null || message.AuthorKind != MessageAuthorKind.Operator)
        {
            return ConversationErrors.MessageNotFound(command.MessageId.Value);
        }

        var now = clock.UtcNow;

        // `adr/0020`: a redelivered ack against an already-delivered message is a harmless no-op -
        // Message.MarkDelivered's own idempotency is what answers this, checked before opening a
        // transaction at all, since there is nothing to write or publish either way.
        if (!message.MarkDelivered(now))
        {
            return Result.Success();
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // `25-119`: message.AuthorId is already this message's own operator - MarkDelivered above just
        // proved AuthorKind == Operator, so this is never the visitor or the system sentinel.
        outbox.Enqueue(MessageDeliveredMapper.ToEnvelope(
            conversation.Id, message.Id, new OperatorId(message.AuthorId), now, idGenerator));

        await conversations.SaveAsync(conversation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }
}
