namespace Ago.Chat.Domain;

/// <summary>
/// <c>Pending -&gt; Waiting -&gt; Assigned -&gt; Closed</c>, enforced by <see cref="Conversation"/>'s
/// methods, never settable directly (data-model.md).
///
/// <para>`25-221`: <see cref="Pending"/> is new - every brand-new conversation starts there
/// (<see cref="Conversation.Start"/>) and never enters <see cref="Waiting"/> until the visitor's own
/// first real message arrives (<see cref="Conversation.AddVisitorMessage"/>). Stored as text
/// (<c>HasConversion&lt;string&gt;()</c>, <c>ConversationConfiguration</c>), so adding this member
/// needed no migration - the column already holds whatever member name is written to it.</para>
/// </summary>
public enum ConversationState
{
    /// <summary>
    /// `25-221`: a conversation that exists (a widget mounted, or a channel message created its row)
    /// but that the visitor has never actually written into. Deliberately excluded from every
    /// operator-facing read and from <c>ConversationAssignmentJob</c>'s claim query by construction -
    /// both already filter on <see cref="Waiting"/>/<see cref="Assigned"/> literally, so a
    /// <see cref="Pending"/> row needs no new exclusion added anywhere, it is simply never selected.
    /// See <see cref="Conversation.Start"/>'s own remarks for why opening the widget alone must not be
    /// "a conversation" from an operator's point of view.
    /// </summary>
    Pending,
    Waiting,
    Assigned,
    Closed,
}
