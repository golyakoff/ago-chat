using System.Text.Json;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ResolveAttachmentUploadGrantDelivery;

/// <summary>
/// `25-110`: the product-specific half of realtime.md's Fan-out path for a grant/revoke's own live push -
/// resolve-by-node, publish-per-node mechanics stay entirely in `Ago.Platform.Realtime`'s
/// <see cref="INodeFanoutPublisher"/>, which this handler only calls, the identical shape
/// <c>ResolveConversationAssignmentTargetsHandler</c> already establishes. No conversation load (contrast
/// <c>ResolveMessageDeliveryTargetsHandler</c>): the event already carries the one recipient this fact
/// concerns.
/// </summary>
public sealed class ResolveAttachmentUploadGrantDeliveryTargetsHandler(INodeFanoutPublisher fanout)
{
    public async Task<Result> HandleAsync(
        ResolveAttachmentUploadGrantDeliveryTargets command, CancellationToken cancellationToken)
    {
        var recipients = new List<PrincipalKey> { PrincipalKeys.ForVisitor(new VisitorId(command.VisitorId)) };

        var dto = new AttachmentUploadGrantChangedDto(command.ConversationId, command.Granted, command.OccurredAt);
        // `5-11`: must match SignalR's own camelCase hub-protocol default - WireJsonOptions's own doc
        // comment explains why a plain JsonSerializer.Serialize(dto) here would silently ship every
        // field as `undefined` to the client once it survives the JsonElement round-trip.
        const string Method = "AttachmentUploadGrantChanged";
        var fanoutResult = await fanout.PublishAsync(
            recipients, Method, JsonSerializer.Serialize(dto, WireJsonOptions.Options), command.CorrelationId, cancellationToken);

        // `7-08`: instrumented for the same reason every other fan-out handler is, tagged with the same
        // `method` dimension so this stays distinguishable from MessageReceived/ConversationAssigned.
        FanoutObservability.RecordFanout(fanoutResult, Method);

        return Result.Success();
    }
}
