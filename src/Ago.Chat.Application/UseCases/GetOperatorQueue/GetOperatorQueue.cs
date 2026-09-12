using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetOperatorQueue;

/// <summary>A pure query, no business invariant to enforce - see `GetOperatorQueueHandler`'s own
/// remarks on why this use case has no Domain step. <paramref name="Tags"/>: `18-04`'s own queue
/// filter, widened by `25-59` from one tag to every tag the operator selected - <see langword="null"/>
/// or empty means unfiltered, and more than one is AND (a conversation must carry every tag named
/// here), matching the console's own filter and the backlog item's stated contract.</summary>
public sealed record GetOperatorQueue(OperatorId OperatorId, SiteId SiteId, IReadOnlyList<TagId>? Tags = null);
