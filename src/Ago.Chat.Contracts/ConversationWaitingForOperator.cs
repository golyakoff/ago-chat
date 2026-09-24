namespace Ago.Chat.Contracts;

/// <summary>
/// `26-86`'s own notification half: a brand-new visitor conversation entered the queue with nobody
/// assigned yet. Named differently from the domain event `Ago.Chat.Domain.ConversationEnteredQueue` on
/// purpose - the same `MessageAdded`/`MessageAccepted`, `ConversationAssigned`/`ConversationAssignedToOperator`
/// naming split every other domain-event/contract pair in this codebase already uses, so the mapper that
/// uses both types side by side never collides on a bare name. Carries no operator - there is none yet,
/// which is the entire reason this is a third kind rather than a variant of
/// <see cref="ConversationAssignedToOperator"/>.
/// </summary>
public sealed record ConversationWaitingForOperator(
    Guid ConversationId,
    Guid SiteId,
    Guid VisitorId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
