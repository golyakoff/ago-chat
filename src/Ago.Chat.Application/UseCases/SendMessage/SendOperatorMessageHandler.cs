using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
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
///
/// <para><b>`22-08`: the account-wide suspension gate lives here, and deliberately not in
/// `Ago.Chat.Infrastructure.Postgres.Pipeline.MessageBatchWriter`.</b> That writer is shared by
/// visitor and operator traffic alike, and `docs/backlog/22-08-*.md`'s own Scope requires a visitor's
/// inbound message to keep being accepted and stored during a suspension - "operators can read but not
/// send", never "nothing is accepted at all". Gating here, before the message ever reaches the shared
/// writer, is what keeps that distinction real rather than accidental: a visitor's
/// `SendVisitorMessageHandler` never calls <see cref="ISiteSuspensionReadStore"/> at all.</para>
/// </summary>
public sealed class SendOperatorMessageHandler(
    IPermissionChecker permissions,
    ISiteSuspensionReadStore suspensions,
    IMessagePipeline pipeline,
    IClock clock)
{
    public async Task<Result<int>> HandleAsync(SendOperatorMessage command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.AuthorId, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages for this site.");
        }

        // `22-08`: live, never cached - ISiteSuspensionReadStore's own remarks. Checked after the
        // ordinary permission gate (an operator who may not send at all learns that first, not a fact
        // about the account) and before anything about this specific message is inspected.
        if (await suspensions.IsSuspendedAsync(command.SiteId, clock.UtcNow, cancellationToken))
        {
            return ConversationErrors.TenantSuspendedCannotSend();
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
        // `14-06`: see SendVisitorMessageHandler's matching comment. An operator's client can send
        // structured content too - a console offering a canned reply with choices is the obvious
        // case - and nothing about the validation differs by author.
        var content = StructuredContentBinder.Bind(command.ContentKind, command.Payload, command.Actions);
        if (content.IsFailure)
        {
            return content.Error!.Value;
        }

        var pending = new PendingMessage(
            command.ConversationId, MessageAuthorKind.Operator, command.AuthorId.Value, body,
            command.AttachmentId, command.ClientMessageId, command.TraceParent, content.Value);
        return await pipeline.EnqueueAsync(pending, cancellationToken);
    }
}
