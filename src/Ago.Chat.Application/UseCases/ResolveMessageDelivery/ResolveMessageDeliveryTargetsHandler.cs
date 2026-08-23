using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ResolveMessageDelivery;

/// <summary>
/// 3-02: the product-specific half of realtime.md's Fan-out path ("who are the participants of this
/// conversation, and what payload do they get") - the resolve-by-node, publish-per-node mechanics
/// stay entirely in <c>Ago.Platform.Realtime</c>'s <see cref="INodeFanoutPublisher"/>, which this
/// handler only calls.
///
/// Reads via <see cref="IConversationReadStore"/> directly, not through
/// <c>GetConversationHistoryHandler</c>'s visitor/operator-authorized entry points: this handler is
/// not acting on behalf of either - it already trusts the conversation exists (just loaded via
/// <see cref="IConversationRepository"/>) and reading a message it is about to broadcast is not a
/// permission decision, so routing it through an authorization check designed for an external
/// caller would be a layering fiction, not a real check.
/// </summary>
public sealed class ResolveMessageDeliveryTargetsHandler(
    IConversationRepository conversations,
    IConversationReadStore readStore,
    INodeFanoutPublisher fanout)
{
    public async Task<Result> HandleAsync(ResolveMessageDeliveryTargets command, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        var recipients = new List<PrincipalKey> { PrincipalKeys.ForVisitor(conversation.VisitorId) };
        if (conversation.OperatorId is { } operatorId)
        {
            recipients.Add(PrincipalKeys.ForOperator(operatorId));
        }

        var page = await readStore.GetHistoryAsync(command.ConversationId, command.Sequence + 1, pageSize: 1, cancellationToken);
        var item = page.Messages.Single();
        var dto = new MessageDto(
            item.Id.Value, item.Sequence, item.AuthorKind.ToString(), item.AuthorId, item.Body, item.CreatedAt, item.AttachmentId?.Value);

        await fanout.PublishAsync(recipients, "MessageReceived", JsonSerializer.Serialize(dto, WireJsonOptions.Options), command.CorrelationId, cancellationToken);

        return Result.Success();
    }
}
