using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SendMessage;

public sealed class SendVisitorMessageHandler(
    IConversationRepository conversations,
    IClock clock,
    IIdGenerator idGenerator,
    IOutboxWriter outbox)
{
    public async Task<Result<int>> HandleAsync(SendVisitorMessage command, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        MessageBody body;
        try
        {
            body = new MessageBody(command.Body);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.InvalidBody(ex.Message);
        }

        var now = clock.UtcNow;
        var messageId = new MessageId(idGenerator.NewId(now));

        // The participant check lives in Conversation.AddVisitorMessage itself (1-01) - there is
        // nothing left for this handler to verify beforehand, since a visitor holds no role for
        // IPermissionChecker to look up (adr/0016). Reaching either catch here means the caller's
        // own token/conversation pairing was already stale or wrong by the time it got this far.
        try
        {
            var message = conversation.AddVisitorMessage(command.AuthorId, messageId, body, now);

            // adr/0005: staged here, persisted by the same SaveAsync call below - never a separate
            // transaction, so an outbox row can never exist without the message it describes.
            var domainEvent = conversation.DomainEvents.OfType<MessageAdded>().Last();
            outbox.Enqueue(MessageAcceptedMapper.ToEnvelope(domainEvent, idGenerator));
            conversation.ClearDomainEvents();

            await conversations.SaveAsync(conversation, cancellationToken);
            return message.Sequence;
        }
        catch (ConversationParticipantMismatchException)
        {
            return ConversationErrors.Forbidden("This visitor is not a participant of this conversation.");
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }
    }
}
