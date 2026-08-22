namespace Ago.Chat.Application.UseCases.ResolveConversationAssignment;

/// <summary>The Worker-side reaction to a persisted `ConversationAssignedToOperator` (`4-02`):
/// push the assignment to both participants. Not a write - nothing here changes
/// <c>Conversation</c> state, so there is no domain event and nothing to outbox. Carries
/// <see cref="VisitorId"/>/<see cref="OperatorId"/> directly from the integration event's own
/// payload - unlike <c>ResolveMessageDeliveryTargets</c>, there is no conversation to load here,
/// since the event already names both recipients.
///
/// Named <c>...Targets</c>, not just <c>ResolveConversationAssignment</c> matching the folder, for
/// the same shadowing reason <c>ResolveMessageDeliveryTargets</c> already documents.</summary>
public sealed record ResolveConversationAssignmentTargets(
    Guid ConversationId, Guid VisitorId, Guid OperatorId, DateTimeOffset OccurredAt, Guid CorrelationId);
