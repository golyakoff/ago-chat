namespace Ago.Chat.Contracts;

/// <summary>
/// docs/architecture/messaging.md's <c>MessageAccepted</c> topic, keyed by <see cref="ConversationId"/>.
/// No message body - a consumer that needs it reads <c>GetConversationHistory</c> instead; the payload
/// stays small on purpose. Mapped from <c>Ago.Chat.Domain.MessageAdded</c> in
/// <c>Ago.Chat.Application/Mapping</c>, never constructed from Domain directly.
/// </summary>
public sealed record MessageAccepted(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid SiteId,
    Guid CorrelationId,
    Guid ConversationId,
    string AuthorKind,
    int Sequence);
