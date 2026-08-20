namespace Ago.Chat.Domain;

public sealed record ConversationAssigned(
    ConversationId ConversationId,
    OperatorId OperatorId,
    DateTimeOffset OccurredAt) : IDomainEvent;
