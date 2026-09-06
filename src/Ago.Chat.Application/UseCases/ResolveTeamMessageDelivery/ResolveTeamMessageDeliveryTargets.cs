using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ResolveTeamMessageDelivery;

/// <summary>`23-32`: the team-chat sibling of <c>ResolveMessageDeliveryTargets</c> - see
/// <c>ResolveTeamMessageDeliveryTargetsHandler</c>'s own remarks for how it differs.</summary>
public sealed record ResolveTeamMessageDeliveryTargets(SiteId SiteId, int Sequence, Guid CorrelationId);
