namespace Ago.Chat.Domain;

public sealed record ConversationReleased(
    ConversationId ConversationId,
    OperatorId PreviousOperatorId,
    DateTimeOffset OccurredAt) : IDomainEvent;
