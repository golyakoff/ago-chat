namespace Ago.Chat.Contracts;

/// <summary>
/// `6-02`'s webhook trigger for `6-05`'s eventual dispatcher - an operator closed the conversation via
/// `POST /api/v1/conversations/{id}/close`. Named differently from the domain event
/// <c>Ago.Chat.Domain.ConversationClosed</c> on purpose - the same domain-event/contract naming split
/// `ConversationAssigned`/`ConversationAssignedToOperator` and `ConversationReleased`/
/// `ConversationReleasedToQueue` already established, so `ConversationClosedMapper` using both types
/// side by side never collides on a bare name (`Ago.Chat.Application.Mapping`, keeping the literal
/// `ConversationClosedMapper` name the backlog item asked for, since the mapper's own namespace has no
/// collision to avoid - only the domain-event/contract pair does). Carries only
/// <see cref="ConversationId"/> and <see cref="ClosedAt"/> - no visitor/operator identity, since a
/// webhook receiver only needs to know which conversation closed, not who closed it (`6-02`'s own
/// scope note).
/// </summary>
public sealed record ConversationEnded(Guid ConversationId, DateTimeOffset ClosedAt);
