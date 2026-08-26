using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SendOfflineAutoReply;

/// <summary>
/// `14-04`: the consumer-side counterpart of <c>MessageAccepted</c> - built from the integration
/// event's own fields (<c>Ago.Chat.Contracts.MessageAccepted</c>), never from Domain directly, since a
/// consumer only ever sees the wire contract. The same shape <c>RecordUnreadMessage</c> already
/// established for the other consumer of this topic.
///
/// <para><see cref="TriggerMessageId"/> is both the message that might deserve a reply and the
/// idempotency key: it is what <see cref="SendOfflineAutoReplyHandler"/> records in the inbox ledger,
/// so a redelivery of this exact event cannot produce a second reply.</para>
///
/// <para><see cref="TriggerAuthorKind"/> is carried rather than looked up because it is already on the
/// wire and because the loop guard should be able to refuse a message without touching the database at
/// all - see the handler's own remarks.</para>
///
/// <para>No message body: <c>MessageAccepted</c> deliberately carries none, and this use case reads
/// the text it matches against from the conversation aggregate it has to load anyway.</para>
/// </summary>
public sealed record SendOfflineAutoReply(
    Guid TriggerMessageId,
    SiteId SiteId,
    ConversationId ConversationId,
    MessageAuthorKind TriggerAuthorKind,
    int TriggerSequence);
