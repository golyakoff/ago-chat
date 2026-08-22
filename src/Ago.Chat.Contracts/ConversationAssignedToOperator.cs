namespace Ago.Chat.Contracts;

/// <summary>
/// `4-02`'s notification half of the automated assignment engine (`docs/vision.md`: "...assignment
/// engine picks a free operator respecting their capacity -> both sides are notified"). Named
/// differently from <c>Ago.Chat.Domain.ConversationAssigned</c> on purpose - the same
/// domain-event/contract naming split `MessageAdded`/`MessageAccepted` already established, so the
/// mapper that uses both types side by side never collides on a bare name. Carries
/// <see cref="VisitorId"/> directly rather than making a fan-out consumer look it up - the resolve
/// step this event feeds needs no conversation load beyond what it already has. Mapped from
/// <c>Ago.Chat.Domain.ConversationAssigned</c> in <c>Ago.Chat.Application/Mapping</c>, never
/// constructed from Domain directly.
/// </summary>
public sealed record ConversationAssignedToOperator(
    Guid ConversationId,
    Guid SiteId,
    Guid VisitorId,
    Guid OperatorId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
