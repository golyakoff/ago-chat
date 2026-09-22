using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.NotifyOperatorDevices;

/// <summary>
/// `26-05`/`push-notifications.md`'s own "Fan-out" section: the Worker-side reaction to a persisted
/// `ConversationAssignedToOperator` (`OperatorAssignmentPushConsumer`). Everything this command needs
/// is already on the wire contract - no conversation load, the identical property
/// <c>ResolveConversationAssignmentTargets</c>'s own doc comment already states for the realtime
/// counterpart of this same event. Covers a transfer for free: `ConversationTransferredMapper` maps
/// `Ago.Chat.Domain.ConversationTransferred` onto this same `ConversationAssignedToOperator` contract,
/// so a transfer reaches this command exactly as a first assignment does - there is no second command
/// type and no second consumer for "the conversation moved to someone new."
/// </summary>
public sealed record NotifyOperatorDeviceForAssignment(ConversationId ConversationId, VisitorId VisitorId, OperatorId OperatorId);

/// <summary>
/// `26-05`: the Worker-side reaction to a persisted `MessageAccepted`
/// (`OperatorMessagePushConsumer`). Unlike the assignment command above, the event names no operator -
/// `NotifyOperatorDevicesHandler.HandleMessageAsync` loads the conversation to find one, exactly as
/// `ResolveMessageDeliveryTargetsHandler` already does for the same event
/// (`push-notifications.md`'s own "What the handler decides" section, restated from `adr/0179`/
/// `adr/0180` §2). <see cref="AuthorKind"/> travels as the contract's own raw string (never
/// <c>Domain.MessageAuthorKind</c> - `Ago.Chat.Contracts` cannot reference `Ago.Chat.Domain`, the
/// identical wire-shape reason <c>RecordUnreadMessage</c>'s own command parses it from a string too),
/// so the handler is the one place that turns "was this from a visitor" into a decision.
/// </summary>
public sealed record NotifyOperatorDeviceForMessage(ConversationId ConversationId, MessageId MessageId, string AuthorKind);
