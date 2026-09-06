using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ResolveTeamMessageRemovalDelivery;

/// <summary>
/// `23-33`: the removal-fanout sibling of
/// <c>Ago.Chat.Application.UseCases.ResolveTeamMessageDelivery.ResolveTeamMessageDeliveryTargetsHandler</c>
/// - identical recipients (every active operator of the site, unconditionally, including the remover -
/// that class's own remarks give the full reasoning, unchanged here) and an identical "re-read the
/// row rather than build a DTO from memory" shape, so the tombstone every operator sees is
/// byte-identical regardless of whether it arrived as this call's own fan-out or a page's own reload.
///
/// <para><b>A separate class, not that one reused with a <c>Method</c> parameter.</b> The two push
/// under different SignalR client method names - this one under <c>TeamMessageRemoved</c>, never
/// <c>TeamMessageReceived</c> - because the console's own transport-level dedup
/// (`SeenMessageIds`, keyed by message id) would otherwise silently drop this push: that dedup exists
/// to collapse an operator's own local echo of a *new* post against its fan-out copy, and a removal is
/// not a second delivery of the same post (<c>Ago.Chat.Contracts.TeamMessageRemoved</c>'s own remarks
/// state this in full). Parameterising one handler's push method name by an enum or a string would
/// hide that distinction behind a flag rather than naming it in two small, separate, self-explanatory
/// classes - the same "no generalisation ahead of a second, genuinely different need" restraint
/// <c>Ago.Chat.Domain.TeamMessage</c>'s own remarks state for declining a shared aggregate base.</para>
/// </summary>
public sealed class ResolveTeamMessageRemovalDeliveryTargetsHandler(
    ITeamMessageReadStore readStore,
    IOperatorTeamReadStore operatorTeam,
    INodeFanoutPublisher fanout)
{
    private const string Method = "TeamMessageRemoved";

    public async Task<Result> HandleAsync(
        ResolveTeamMessageRemovalDeliveryTargets command, CancellationToken cancellationToken)
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
