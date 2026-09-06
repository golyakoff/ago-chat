namespace Ago.Chat.Contracts;

/// <summary>
/// `23-32`: docs/architecture/messaging.md's <c>TeamMessagePosted</c> topic, keyed by
/// <see cref="SiteId"/> - the team-chat sibling of <see cref="MessageAccepted"/>, published for the
/// identical reason: driving realtime fan-out, not carrying the message to a business consumer.
///
/// <para>No message body, matching <see cref="MessageAccepted"/>'s own precedent - a consumer that
/// needs it (<c>TeamChatFanoutConsumer</c>) reads the room's own history instead, so this payload
/// stays small and never carries what an operator wrote to the broker twice. Mapped from raw values
/// in <c>Ago.Chat.Application.Mapping.TeamMessagePostedMapper</c>, called from
/// <c>Ago.Chat.Infrastructure.Postgres.TeamChatRepository</c> - there is no aggregate root to raise a
/// domain event first (<c>Ago.Chat.Domain.TeamMessage</c>'s own remarks on why), so the mapper is
/// handed the already-persisted row's own values directly, the same shape
/// <c>ModuleQuantityGrantedMapper.ToEnvelope</c> already takes for the identical reason.</para>
/// </summary>
public sealed record TeamMessagePosted(
    Guid TeamMessageId,
    DateTimeOffset OccurredAt,
    Guid SiteId,
    Guid CorrelationId,
    int Sequence);
