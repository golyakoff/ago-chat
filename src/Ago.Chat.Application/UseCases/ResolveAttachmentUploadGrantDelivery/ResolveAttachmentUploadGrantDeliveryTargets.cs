namespace Ago.Chat.Application.UseCases.ResolveAttachmentUploadGrantDelivery;

/// <summary>The Worker-side reaction to a persisted `AttachmentUploadGrantChanged` (`25-110`): push the
/// grant/revoke to the one visitor connection holding this conversation open. Carries
/// <see cref="VisitorId"/> directly from the integration event's own payload - like
/// <c>ResolveConversationAssignmentTargets</c>, and unlike <c>ResolveMessageDeliveryTargets</c>, there is
/// no conversation to load here: the event already names the one recipient (an operator who just clicked
/// the toggle already knows the outcome from their own request's response, so there is no second
/// recipient to resolve the way a chat message has one).
///
/// Named <c>...Targets</c>, not just <c>ResolveAttachmentUploadGrantDelivery</c> matching the folder, for
/// the same shadowing reason <c>ResolveMessageDeliveryTargets</c>'s own remarks already document.</summary>
public sealed record ResolveAttachmentUploadGrantDeliveryTargets(
    Guid ConversationId, Guid VisitorId, bool Granted, DateTimeOffset OccurredAt, Guid CorrelationId);
