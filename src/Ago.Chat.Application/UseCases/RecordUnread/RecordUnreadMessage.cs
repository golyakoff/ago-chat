using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RecordUnread;

/// <summary>2-05: the consumer-side counterpart of `MessageAccepted` - built from the integration
/// event's own fields (`Ago.Chat.Contracts.MessageAccepted`), never from Domain directly, since a
/// consumer only ever sees the wire contract.
///
/// `5-15`: <paramref name="Sequence"/> was already on the wire (`MessageAccepted.Sequence`) and simply
/// not carried across - no contract change, no new event version. It is what lets
/// <see cref="Domain.Conversation.IncrementUnreadCount"/> tell "the operator has already read this
/// one" from "this is new", which is the whole basis of `5-15`'s clear-up-to-a-watermark.</summary>
public sealed record RecordUnreadMessage(
    Guid MessageId, ConversationId ConversationId, MessageAuthorKind AuthorKind, int Sequence);
