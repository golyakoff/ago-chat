namespace Ago.Chat.Contracts;

/// <summary>
/// `25-119`: the widget's own delivery ack, raised once <see cref="Domain.Message.MarkDelivered"/>
/// actually transitions (never for a redundant re-ack - <c>AcknowledgeMessageDeliveredHandler</c>'s own
/// remarks state why). Modelled on `25-110`'s <see cref="AttachmentUploadGrantChanged"/>: carries
/// <see cref="OperatorId"/> directly, not just <see cref="ConversationId"/>, so the one Worker consumer
/// this event has (<c>ResolveMessageDeliveredTargetsHandler</c>) needs no database read at all to name
/// the one live connection to reach (<c>PrincipalKeys.ForOperator</c>) - unlike `3-02`'s own
/// <c>ResolveMessageDeliveryTargetsHandler</c>, which does load the conversation, because that one
/// resolves *two* possible recipients (visitor and operator) from a plain <c>MessageAccepted</c> that
/// carries neither. This event only ever has one recipient - the message's own author - so the fact is
/// already on hand at the point <c>AcknowledgeMessageDeliveredHandler</c> builds it, and putting it on
/// the wire costs nothing a database round trip would otherwise buy back.
/// </summary>
public sealed record MessageDelivered(
    Guid ConversationId, Guid MessageId, Guid OperatorId, DateTimeOffset DeliveredAt);
