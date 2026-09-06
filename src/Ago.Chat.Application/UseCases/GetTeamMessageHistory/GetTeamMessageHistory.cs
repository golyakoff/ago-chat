using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetTeamMessageHistory;

/// <summary><see cref="SiteId"/> is the caller's own operator claim, never a route segment or a body
/// value - the room this reads is always the caller's own site's, so there is nothing else for a
/// caller to name (the same shape `GetOperatorQueueHandler`'s own remarks describe for a query with
/// no permission to check: "reads are keyed by site and by the caller's own id").</summary>
public sealed record GetTeamMessageHistory(SiteId SiteId, int? BeforeSequence, int PageSize);

/// <summary>`3-03`'s reconnect delta, the team-chat sibling of <c>GetConversationDeltaAsOperator</c> -
/// every message strictly after <paramref name="AfterSequence"/>, oldest first.</summary>
public sealed record GetTeamMessageDelta(SiteId SiteId, int AfterSequence);
