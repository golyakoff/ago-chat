using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SendMessage;

/// <summary>
/// `4-05`: same split as `SendVisitorMessageHandler` - the write moved to
/// `Ago.Chat.Api.Pipeline.MessageBatchWriter`, batched off a queue behind `IMessagePipeline`
/// (concurrency.md's "In-process pipeline (Api)"). Unlike the visitor handler, there is *no*
/// pre-enqueue conversation load here at all - RBAC's `SiteId` comes straight from the operator's
/// own token claims (`SendOperatorMessage`'s own doc comment), not from a lookup, and there is no
/// per-operator/per-site rate limit on this path (`3-05` scoped rate limiting to the visitor side
/// only). `NotFound` and the participant/state checks `AddOperatorMessage` enforces are all
/// discovered inside the pipeline worker instead of here.
/// </summary>
public sealed class SendOperatorMessageHandler(
    IPermissionChecker permissions,
    IMessagePipeline pipeline)
{
    public async Task<Result<int>> HandleAsync(SendOperatorMessage command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.AuthorId, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages for this site.");
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

        // The RBAC check above answers "may this operator send at all"; Conversation.AddOperatorMessage
        // (1-01) still separately checks "is this operator the one assigned to *this* conversation" -
        // a fact about the conversation, not a permission (adr/0016 draws that line) - now enforced
        // inside the pipeline worker once it loads the conversation fresh.
        var pending = new PendingMessage(
            command.ConversationId, MessageAuthorKind.Operator, command.AuthorId.Value, body,
            command.AttachmentId, command.ClientMessageId, command.TraceParent);
        return await pipeline.EnqueueAsync(pending, cancellationToken);
    }
}
