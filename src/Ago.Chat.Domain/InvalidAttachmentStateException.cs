namespace Ago.Chat.Domain;

/// <summary>
/// An operation was attempted against an <see cref="Attachment"/> in a state that cannot legally
/// perform it - mirrors <see cref="InvalidConversationStateException"/>'s own reasoning: by the time
/// this is reached, Application has already resolved the request; a stale client state (e.g.
/// confirming a conversation's attachment twice) is the only way to hit this.
/// </summary>
public sealed class InvalidAttachmentStateException(string message) : Exception(message);
