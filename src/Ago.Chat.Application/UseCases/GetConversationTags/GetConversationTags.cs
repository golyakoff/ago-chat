using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversationTags;

public sealed record GetConversationTags(ConversationId ConversationId, SiteId SiteId, OperatorId RequestedBy);
