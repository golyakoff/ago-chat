namespace Ago.Chat.Domain;

/// <summary>
/// `26-86`: the signal `26-18`/`adr/0179` named as missing when they scoped a push for this moment out -
/// "the first has no server-side signal at all - there is no `ConversationStarted` contract to subscribe
/// to." <see cref="ConversationStarted"/> already exists, but fires from <see cref="Conversation.Start"/>,
/// which leaves every new conversation in <see cref="ConversationState.Pending"/> (`25-221`), not
/// <see cref="ConversationState.Waiting"/> - a conversation nothing has read yet, invisible to the queue,
/// is not "someone is waiting." This event is raised instead from <see cref="Conversation.AddVisitorMessage"/>,
/// at the one instant that actually makes a conversation visible to an operator: the
/// <see cref="ConversationState.Pending"/> -&gt; <see cref="ConversationState.Waiting"/> transition
/// triggered by the visitor's own first real message, exactly where `WaitingConversationClaimQuery` and
/// `GetOperatorQueueHandler` both start looking. Named differently from its own conversation-visibility
/// query rather than reusing `ConversationStarted` - a conversation can be released back to `Waiting`
/// later too (<see cref="Conversation.ReleaseToQueue"/>), and that path already raises
/// <see cref="ConversationReleased"/>/`ConversationReleasedToQueue` for the identical "waiting again"
/// fact; this event is deliberately the *first-ever* entry into the queue only, matching this item's own
/// backlog scope ("a new visitor conversation entering Waiting").
///
/// <para><b>Never raised for a routing-suppressed conversation.</b> <see cref="Conversation.IsRoutingSuppressed"/>
/// conversations accept visitor messages exactly like any other (`RoutingSuppressedAt`'s own remarks:
/// "the conversation is created exactly as normal in every other respect... still accepts and stores the
/// visitor's own messages"), but must never reach an operator's queue at all - raising this event for one
/// would push exactly the attention `23-69`/`23-77`'s own silence guarantee exists to withhold. The
/// aggregate is the only place that can make this call for free, since <see cref="Conversation.IsRoutingSuppressed"/>
/// is already its own state; no caller needs to re-check it.</para>
/// </summary>
public sealed record ConversationEnteredQueue(
    ConversationId ConversationId,
    SiteId SiteId,
    VisitorId VisitorId,
    DateTimeOffset OccurredAt) : IDomainEvent;
