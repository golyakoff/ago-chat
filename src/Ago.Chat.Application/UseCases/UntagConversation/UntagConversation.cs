using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UntagConversation;

public sealed record UntagConversation(ConversationId ConversationId, SiteId SiteId, TagId TagId, OperatorId RequestedBy);
