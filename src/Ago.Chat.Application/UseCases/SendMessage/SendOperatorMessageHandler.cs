using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SendMessage;

public sealed class SendOperatorMessageHandler(
    IConversationRepository conversations,
    IPermissionChecker permissions,
    IClock clock,
    IIdGenerator idGenerator)
{
    public async Task<Result<int>> HandleAsync(SendOperatorMessage command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.AuthorId, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages for this site.");
        }

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

        // The RBAC check above answers "may this operator send at all"; Conversation.AddOperatorMessage
        // (1-01) still separately checks "is this operator the one assigned to *this* conversation" -
        // a fact about the conversation, not a permission (adr/0016 draws that line).
        try
        {
            var message = conversation.AddOperatorMessage(command.AuthorId, messageId, body, now);
            await conversations.SaveAsync(conversation, cancellationToken);
            return message.Sequence;
        }
        catch (ConversationParticipantMismatchException)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }
    }
}
