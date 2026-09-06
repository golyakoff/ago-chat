using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ResolveTeamMessageDelivery;

/// <summary>
/// `23-32`: the team-chat sibling of <c>ResolveMessageDeliveryTargetsHandler</c> - the product-specific
/// half of realtime.md's Fan-out path ("who are the room's participants, and what payload do they
/// get"), called by <c>Ago.Chat.Worker.TeamChatFanoutConsumer</c> reacting to
/// <see cref="TeamMessagePosted"/>.
///
/// <para><b>Recipients: every active operator of the site, unconditionally - including the sender.</b>
/// An ordinary conversation message's own recipient list is the two participants
/// (<c>ResolveMessageDeliveryTargetsHandler</c>'s visitor + assigned operator); a team room has no
/// such pairing - "every operator of that tenant in it" (the backlog item's own Scope) is exactly
/// <see cref="IOperatorTeamReadStore.GetForSiteAsync"/>'s own definition. Including the sender is
/// deliberate, not an oversight: the sender's *other* connections (a second tab, a reconnect) need
/// the push too, and <c>OperatorConnection</c>'s own <c>SeenMessageIds</c> dedup already discards the
/// echo on whichever connection sent it - the same shape <c>ResolveMessageDeliveryTargetsHandler</c>
/// relies on for an operator's own other tabs today.</para>
/// </summary>
public sealed class ResolveTeamMessageDeliveryTargetsHandler(
    ITeamMessageReadStore readStore,
    IOperatorTeamReadStore operatorTeam,
    INodeFanoutPublisher fanout)
{
    private const string Method = "TeamMessageReceived";

    public async Task<Result> HandleAsync(ResolveTeamMessageDeliveryTargets command, CancellationToken cancellationToken)
    {
        var item = await readStore.GetBySequenceAsync(command.SiteId, command.Sequence, cancellationToken);
        if (item is null)
        {
            return TeamChatErrors.NotFound($"Team message with sequence {command.Sequence} was not found for the site.");
        }

        var members = await operatorTeam.GetForSiteAsync(command.SiteId, cancellationToken);
        var recipients = members.Select(m => PrincipalKeys.ForOperator(m.OperatorId)).ToList();

        var dto = TeamMessageDtoMapper.ToDto(item);
        var fanoutResult = await fanout.PublishAsync(
            recipients, Method, JsonSerializer.Serialize(dto, WireJsonOptions.Options), command.CorrelationId, cancellationToken);

        FanoutObservability.RecordFanout(fanoutResult, Method);

        return Result.Success();
    }
}
