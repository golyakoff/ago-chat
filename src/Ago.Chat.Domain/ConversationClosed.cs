namespace Ago.Chat.Domain;

public sealed record ConversationClosed(
    ConversationId ConversationId,
    DateTimeOffset OccurredAt) : IDomainEvent;
