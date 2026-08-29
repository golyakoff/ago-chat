using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AddConversationNote;

public sealed record AddConversationNote(ConversationId ConversationId, SiteId SiteId, OperatorId RequestedBy, string Body);
