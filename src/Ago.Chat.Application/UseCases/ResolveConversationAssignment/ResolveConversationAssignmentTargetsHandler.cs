using System.Text.Json;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ResolveConversationAssignment;

/// <summary>
/// `4-02`: the product-specific half of realtime.md's Fan-out path for the assignment engine's own
/// notification - resolve-by-node, publish-per-node mechanics stay in `Ago.Platform.Realtime`'s
/// <see cref="INodeFanoutPublisher"/>, which this handler only calls. No conversation load
/// (contrast <c>ResolveMessageDeliveryTargetsHandler</c>): the event already carries both recipients.
/// </summary>
public sealed class ResolveConversationAssignmentTargetsHandler(INodeFanoutPublisher fanout)
{
    public async Task<Result> HandleAsync(ResolveConversationAssignmentTargets command, CancellationToken cancellationToken)
    {
        var recipients = new List<PrincipalKey>
        {
            PrincipalKeys.ForVisitor(new VisitorId(command.VisitorId)),
            PrincipalKeys.ForOperator(new OperatorId(command.OperatorId)),
        };

        var dto = new ConversationAssignedDto(command.ConversationId, command.OperatorId, command.OccurredAt);
        // `5-11`: must match SignalR's own camelCase hub-protocol default - see WireJsonOptions's own
        // doc comment for why a plain JsonSerializer.Serialize(dto) here would silently ship every
        // field as `undefined` to the client once it survives the JsonElement round-trip.
        const string Method = "ConversationAssigned";
        var fanoutResult = await fanout.PublishAsync(
            recipients, Method, JsonSerializer.Serialize(dto, WireJsonOptions.Options), command.CorrelationId, cancellationToken);

        // `7-08`: instrumented for the same reason the message path is, and tagged with the same
        // `method` dimension so the two fan-outs stay distinguishable. An operator who was just
        // assigned a conversation and has no live connection is a different, and more interesting,
        // fact than a visitor who has none.
        FanoutObservability.RecordFanout(fanoutResult, Method);

        return Result.Success();
    }
}
