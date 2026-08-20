namespace Ago.Chat.Domain;

public sealed record ConversationStarted(
    ConversationId ConversationId,
    SiteId SiteId,
    VisitorId VisitorId,
    DateTimeOffset OccurredAt) : IDomainEvent;
