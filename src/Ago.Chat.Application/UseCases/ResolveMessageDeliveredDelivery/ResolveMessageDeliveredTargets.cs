namespace Ago.Chat.Application.UseCases.ResolveMessageDeliveredDelivery;

/// <summary>The Worker-side reaction to a persisted `MessageDelivered` (`25-119`): push the delivery ack
/// straight to the one operator connection that authored the message. Carries <c>OperatorId</c> directly
/// from the integration event's own payload - like <c>ResolveAttachmentUploadGrantDeliveryTargets</c>,
/// and unlike <c>ResolveMessageDeliveryTargets</c>, there is no conversation to load here: the event
/// already names the one recipient.
///
/// Named <c>...Targets</c>, not just <c>ResolveMessageDeliveredDelivery</c> matching the folder, for the
/// same shadowing reason <c>ResolveMessageDeliveryTargets</c>'s own remarks already document.</summary>
public sealed record ResolveMessageDeliveredTargets(
    Guid ConversationId, Guid MessageId, Guid OperatorId, DateTimeOffset DeliveredAt, Guid CorrelationId);
