using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RecordUnread;

/// <summary>2-05: the consumer-side counterpart of `MessageAccepted` - built from the integration
/// event's own fields (`Ago.Chat.Contracts.MessageAccepted`), never from Domain directly, since a
/// consumer only ever sees the wire contract.</summary>
public sealed record RecordUnreadMessage(Guid MessageId, ConversationId ConversationId, MessageAuthorKind AuthorKind);
