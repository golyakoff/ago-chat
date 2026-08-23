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
        await fanout.PublishAsync(recipients, "ConversationAssigned", JsonSerializer.Serialize(dto, WireJsonOptions.Options), command.CorrelationId, cancellationToken);

        return Result.Success();
    }
}
