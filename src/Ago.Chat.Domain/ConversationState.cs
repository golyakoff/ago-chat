namespace Ago.Chat.Domain;

/// <summary>
/// <c>Waiting -&gt; Assigned -&gt; Closed</c>, enforced by <see cref="Conversation"/>'s methods, never
/// settable directly (data-model.md).
/// </summary>
public enum ConversationState
{
    Waiting,
    Assigned,
    Closed,
}
