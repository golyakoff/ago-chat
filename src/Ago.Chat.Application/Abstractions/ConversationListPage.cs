namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `5-08`: a keyset page over every conversation for a site, newest-first (data-model.md: `OFFSET`
/// is banned - the same reasoning <see cref="ConversationHistoryPage"/> already applies to message
/// history applies here, since a site's conversation history has no bound either; it only grows).
/// <see cref="NextBeforeId"/> is <see langword="null"/> once the caller has reached the oldest
/// conversation. Cursor is a conversation id, not a sequence number - conversation ids are uuid v7
/// (<c>IIdGenerator</c>), so id order already is creation order, without a second column to carry.
/// </summary>
public sealed record ConversationListPage(
    IReadOnlyList<ConversationSummaryItem> Conversations, Guid? NextBeforeId);
