namespace Ago.Chat.Domain;

/// <summary>
/// Raised by both <see cref="Conversation.AddVisitorMessage"/> and
/// <see cref="Conversation.AddOperatorMessage"/> - one event regardless of author kind, since nothing
/// downstream needs a different shape per side (`messaging.md`'s <c>MessageAccepted</c> topic has one
/// consumer set, not two). Mapped to the <c>MessageAccepted</c> integration event in
/// <c>Ago.Chat.Application/Mapping</c> - never serialized directly (`clean-architecture.md`).
/// </summary>
public sealed record MessageAdded(
    MessageId MessageId,
    ConversationId ConversationId,
    SiteId SiteId,
    int Sequence,
    MessageAuthorKind AuthorKind,
    DateTimeOffset OccurredAt) : IDomainEvent;
