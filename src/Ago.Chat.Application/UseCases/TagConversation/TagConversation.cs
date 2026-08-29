using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.TagConversation;

public sealed record TagConversation(ConversationId ConversationId, SiteId SiteId, TagId TagId, OperatorId RequestedBy);
