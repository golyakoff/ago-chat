using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetOperatorQueue;

/// <summary>A pure query, no business invariant to enforce - see `GetOperatorQueueHandler`'s own
/// remarks on why this use case has no Domain step.</summary>
public sealed record GetOperatorQueue(OperatorId OperatorId, SiteId SiteId);
