namespace Ago.Chat.Contracts;

/// <summary>The realtime protocol's wire shape for one message (api-design.md: "payload shapes live
/// in Ago.Chat.Contracts and are versioned with the same additive-only rule as integration
/// events").
///
/// `5-07`: <see cref="ClientMessageId"/> is additive - appended last, optional, `null` for any
/// message sent before this shipped (nothing back-filled it). It rides along on every delivery of a
/// message, including the fan-out copy other participants receive, mostly so the *sender's own*
/// other tabs/reconnects can reconcile an optimistic send against its real ack; another participant
/// has no reason to care and simply ignores it.
///
/// `5-07`: <see cref="ConversationId"/> is also new, and turned out to be load-bearing, not cosmetic
/// - found while building the console. `SendMessageAsync`/`JoinConversationAsync`'s local echo and
/// history reads were always implicitly scoped to one conversation because their *caller* already
/// knew which one; the real fan-out push (`ConnectionFanoutConsumer` -> `NodeFanoutPublisher` ->
/// `SignalRConnectionDispatcher`) delivers straight to a connection with no SignalR group involved,
/// so a `MessageReceived` event for *any* conversation an operator is assigned to lands on their one
/// connection, indistinguishable from every other conversation's messages, without this field. The
/// widget never noticed because a visitor only ever has one conversation; an operator routinely has
/// several open at once (their own capacity), which is exactly the case this closes. JSON property
/// order is irrelevant to a caller matching by name, so this being appended last costs nothing -
/// unlike a hub *method* parameter, a DTO's wire shape is not positional.</summary>
/// `14-06`: <see cref="ContentKind"/>, <see cref="Content"/> and <see cref="Actions"/> are additive
/// in exactly the sense api-design.md requires - appended last, all optional, `null` on every message
/// sent before this shipped and on every prose message after it. A client built before this ignores
/// three fields it has never heard of and keeps rendering <see cref="Body"/>, which is the whole
/// reason <see cref="Body"/> stays mandatory for structured messages too.
///
/// <see cref="Content"/> is a <see cref="System.Text.Json.JsonElement"/>, not a string: it is
/// already JSON, and sending it as a string would make every client parse a string out of a document
/// it had just parsed. It is also the one field on this DTO that AGO Chat has no schema for - a
/// client is free to have one; this product is not.
public sealed record MessageDto(
    Guid Id, int Sequence, string AuthorKind, Guid AuthorId, string Body, DateTimeOffset CreatedAt,
    Guid? AttachmentId = null, Guid? ClientMessageId = null, Guid? ConversationId = null,
    string? ContentKind = null, System.Text.Json.JsonElement? Content = null,
    IReadOnlyList<MessageActionDto>? Actions = null);
