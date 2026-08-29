namespace Ago.Chat.Domain;

/// <summary>
/// `18-02`: raised by <see cref="Conversation.TransferTo"/> - a conversation that was already
/// <see cref="ConversationState.Assigned"/> to one operator moves to a named colleague without ever
/// leaving <c>Assigned</c> (unlike <see cref="ConversationReleased"/>, which takes it back to
/// <c>Waiting</c> first). Carries both operators, unlike <see cref="ConversationAssigned"/> which only
/// ever had one to carry - <see cref="FromOperatorId"/> is not on the wire contract this maps to today
/// (<c>ConversationTransferredMapper</c> deliberately reuses <c>ConversationAssignedToOperator</c> so
/// every existing consumer of "this conversation now has this assigned operator" - the realtime
/// fan-out to both participants, the assignment webhook - keeps working with no new wiring), but it
/// belongs on the domain event itself: a future audit trail or a dedicated transfer notification is a
/// mapping change away, not a re-plumbing of <see cref="Conversation"/>.
/// </summary>
public sealed record ConversationTransferred(
    ConversationId ConversationId,
    OperatorId FromOperatorId,
    OperatorId ToOperatorId,
    DateTimeOffset OccurredAt) : IDomainEvent;
