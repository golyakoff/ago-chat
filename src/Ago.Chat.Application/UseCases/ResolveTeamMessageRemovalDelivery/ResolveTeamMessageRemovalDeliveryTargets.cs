using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ResolveTeamMessageRemovalDelivery;

/// <summary>`23-33`: the removal-fanout sibling of
/// <c>Ago.Chat.Application.UseCases.ResolveTeamMessageDelivery.ResolveTeamMessageDeliveryTargets</c> -
/// see <see cref="ResolveTeamMessageRemovalDeliveryTargetsHandler"/>'s own remarks for why this is a
/// distinct command/handler rather than that one reused with a branch.</summary>
public sealed record ResolveTeamMessageRemovalDeliveryTargets(SiteId SiteId, int Sequence, Guid CorrelationId);
