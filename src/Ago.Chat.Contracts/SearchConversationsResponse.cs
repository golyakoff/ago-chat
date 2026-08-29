namespace Ago.Chat.Contracts;

/// <summary>
/// `18-01`: `GET /api/v1/conversations/search`'s response body. <see cref="SearchedFrom"/>/
/// <see cref="SearchedTo"/> are the bound this search actually used - always present, even when the
/// operator supplied neither and the handler defaulted them, so the console can show the window it
/// searched rather than the caller having to guess it from what it sent (`18-01`'s own Done-when:
/// "the bound is visible, not silent").
/// </summary>
public sealed record SearchConversationsResponse(
    IReadOnlyList<ConversationSearchResultDto> Results,
    Guid? NextBeforeMessageId,
    DateTimeOffset SearchedFrom,
    DateTimeOffset SearchedTo);
