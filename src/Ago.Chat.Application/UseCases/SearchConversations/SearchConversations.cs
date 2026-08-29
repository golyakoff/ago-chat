using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SearchConversations;

/// <summary>
/// `18-01`: the operator's own search. <paramref name="From"/>/<paramref name="To"/> are the
/// operator-supplied half of the bound decision - <see langword="null"/> for either means "use the
/// handler's own default window" (<see cref="SearchConversationsHandler.DefaultWindowMonths"/>), the
/// same "the port takes the resulting timestamp, not a policy" split
/// <c>ListSitesForOwnerHandler.RecentWindowDays</c> already establishes for an analogous bounded read.
/// </summary>
public sealed record SearchConversations(
    OperatorId RequestedBy,
    SiteId SiteId,
    string Phrase,
    DateTimeOffset? From,
    DateTimeOffset? To,
    Guid? BeforeMessageId,
    int PageSize);
