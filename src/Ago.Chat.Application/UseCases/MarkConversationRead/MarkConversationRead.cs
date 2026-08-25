using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.MarkConversationRead;

/// <summary>
/// `5-15`: "this operator has read this conversation up to <paramref name="UpToSequence"/>".
///
/// The sequence is a parameter rather than implied ("clear everything") on purpose - it is the newest
/// message the caller actually has, so the write clears exactly what was seen and a visitor message
/// arriving in the same instant survives it. See <see cref="Conversation.MarkReadByOperator"/> for the
/// full reasoning.
/// </summary>
public sealed record MarkConversationRead(
    ConversationId ConversationId, OperatorId OperatorId, SiteId SiteId, int UpToSequence);

/// <summary>The conversation's unread state after the write - returned so the caller can update its
/// badge from the server's own answer instead of guessing, and so a no-op mark-read still tells the
/// truth rather than an assumed zero.</summary>
public sealed record MarkConversationReadResult(int OperatorUnreadCount, int OperatorLastReadSequence);
