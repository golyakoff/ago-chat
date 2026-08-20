namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// A keyset page, newest-first (data-model.md: <c>OFFSET</c> is banned). <see
/// cref="NextBeforeSequence"/> is <see langword="null"/> once the caller has reached the start of the
/// conversation.
/// </summary>
public sealed record ConversationHistoryPage(
    IReadOnlyList<MessageHistoryItem> Messages, int? NextBeforeSequence);
