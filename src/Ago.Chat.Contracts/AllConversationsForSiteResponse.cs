namespace Ago.Chat.Contracts;

/// <summary>
/// `5-08`: `GET /api/v1/conversations/all`'s response body - the admin/supervisor site-wide list,
/// keyset-paginated (`ConversationListPage`'s own remarks). Deliberately its own response type
/// rather than reusing `OperatorQueueResponse`'s two-list shape - this view answers a different
/// question ("everything for this site, one page at a time"), not "what's waiting, what's mine".
/// </summary>
public sealed record AllConversationsForSiteResponse(
    IReadOnlyList<ConversationSummaryDto> Conversations, Guid? NextBeforeId);
