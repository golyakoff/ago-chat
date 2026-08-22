namespace Ago.Chat.Contracts;

/// <summary>
/// `4-04`'s notification half: the visitor's operator disconnected and did not return within the
/// grace period, so the conversation is back in the waiting queue for `4-02`'s assignment engine to
/// pick up again. Named differently from the domain event `Ago.Chat.Domain.ConversationReleased` on
/// purpose - the same `MessageAdded`/`MessageAccepted` naming split `3-02` established, so the
/// mapper using both types side by side never collides on a bare name. Only the visitor is notified
/// (`docs/vision.md`'s "either side disconnects... conversation is released back to the queue" -
/// the disconnecting operator has no reason to be told their own disconnect was noticed).
/// </summary>
public sealed record ConversationReleasedToQueue(Guid ConversationId, Guid SiteId, Guid VisitorId, Guid CorrelationId, DateTimeOffset OccurredAt);
