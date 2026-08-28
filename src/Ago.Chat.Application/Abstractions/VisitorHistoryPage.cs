namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// A keyset page over one visitor's past conversations, newest-first (data-model.md: `OFFSET` is
/// banned - the same reasoning <see cref="ConversationListPage"/> already applies to the site-wide
/// list applies here). <see cref="NextBeforeId"/> is <see langword="null"/> once the caller has
/// reached the visitor's oldest conversation.
/// </summary>
public sealed record VisitorHistoryPage(
    IReadOnlyList<VisitorHistoryItem> Conversations, Guid? NextBeforeId);
