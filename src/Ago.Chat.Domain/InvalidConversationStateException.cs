namespace Ago.Chat.Domain;

/// <summary>
/// An operation was attempted against a <see cref="Conversation"/> in a state that cannot legally
/// perform it. By the time this reaches <see cref="Conversation"/>, the Application layer has already
/// resolved capacity/permission concerns (adr/0016) - reaching here means the caller's own state was
/// stale, a bug, never an expected user-facing outcome (coding-style.md).
/// </summary>
public sealed class InvalidConversationStateException(string message) : Exception(message);
