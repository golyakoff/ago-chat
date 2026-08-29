namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-01`: a keyset page of full-text search hits, newest-first. Cursor is the matched message's own
/// id (uuid v7, <c>IIdGenerator</c>) - the same "id order is already creation order, no second column
/// to carry" reasoning <see cref="ConversationListPage"/> already uses, applied to messages instead of
/// conversations.
/// </summary>
public sealed record ConversationSearchPage(
    IReadOnlyList<ConversationSearchResultItem> Results, Guid? NextBeforeMessageId);
